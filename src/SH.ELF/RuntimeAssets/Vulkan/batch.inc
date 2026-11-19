// Pixel ownership is per tile. Each pixel consumes primitives in GS order;
// no depth sorting, frame interpolation or timing changes are introduced.
void gs_draw_triangle(int ci,const GsVertex&,const GsVertex&,const GsVertex&);
#ifdef _WIN32
struct VkTriangle {
 float coeff[8],rgba[12],uvq[12],z[4];
 uint32_t tex[4]; int32_t bounds[4]; uint32_t state[4],alpha[4],blend[4],wrap[4];
 int32_t region[4];uint32_t span[4];
};
static_assert(sizeof(VkTriangle)==272,"Vulkan shader ABI");
struct VkFallback { int ci;GsVertex a,b,c;GsState state;uint32_t provenance[32]; };
struct VkTexture { uint64_t key[5];uint32_t offset;TexInfo info;uint32_t palette[512]; };
rojvk::Device g_vk_device;
std::vector<VkTriangle> g_vk_triangles;
std::vector<VkFallback> g_vk_fallback;
std::vector<VkTexture> g_vk_textures;
std::vector<uint32_t> g_vk_texels,g_vk_spans;
std::vector<uint32_t> g_vk_bins[80*28];
bool g_vk_written[16384]{};
bool g_vk_read[16384]{};
bool g_vk_replaying=false;
uint64_t g_vk_batches=0,g_vk_rendered=0,g_vk_cpu=0;
uint64_t g_vk_prepare_ns=0;
uint64_t g_vk_estimated_ns=0;
size_t g_vk_texture_size=0;
struct VkPrepareProfile {
 uint64_t start=std::getenv("PS2_GS_VK_PROFILE")?prof_now():0;
 ~VkPrepareProfile(){if(start)g_vk_prepare_ns+=prof_now()-start;}
};
static bool gs_vk_requested() {
 static const bool selected=[] {
  if(const char* value=std::getenv("PS2_GS_RENDERER"))return std::strcmp(value,"vulkan")==0;
  return ps2_settings().renderer==1;
 }();return selected&&!g_vk_replaying;
}
template <int psm>
static void gs_vk_decode_texture(const TexInfo& info,uint32_t* destination) {
 for(int y=0;y<info.h;y++)for(int x=0;x<info.w;x++)
  destination[size_t(y)*info.w+x]=gs_texel_wrapped<psm>(info,x,y);
}
static void gs_vk_decode_texture(const TexInfo& info,uint32_t* destination) {
 // Select the memory format once per image, outside the texel loop.
 switch(info.psm){
  case 0x00:gs_vk_decode_texture<0x00>(info,destination);break;
  case 0x01:gs_vk_decode_texture<0x01>(info,destination);break;
  case 0x02:case 0x0a:gs_vk_decode_texture<0x02>(info,destination);break;
  case 0x13:gs_vk_decode_texture<0x13>(info,destination);break;
  case 0x14:gs_vk_decode_texture<0x14>(info,destination);break;
  case 0x1b:gs_vk_decode_texture<0x1b>(info,destination);break;
  case 0x24:gs_vk_decode_texture<0x24>(info,destination);break;
  case 0x2c:gs_vk_decode_texture<0x2c>(info,destination);break;
  default:gs_vk_decode_texture<-1>(info,destination);break;
 }
}
void gs_vk_flush() {
 if(g_vk_replaying||g_vk_triangles.empty())return;
 static const bool validate=std::getenv("PS2_GS_VK_VALIDATE")!=nullptr;
 std::vector<uint32_t> initial;
 if(validate)initial.assign(std::begin(g_vram),std::end(g_vram));
 static const uint64_t minimumWork=[] {
  const char* value=std::getenv("PS2_GS_VK_MIN_WORK_US");
  return uint64_t(value?std::clamp(std::atoi(value),0,100000):4000)*1000;
 }();
 const bool useGPU=validate||std::getenv("PS2_GS_VK_SINGLE")||g_vk_estimated_ns>=minimumWork;
 std::vector<uint32_t> work;
 uint32_t push[4]{};
 if(useGPU){
 g_vk_texels.resize(g_vk_texture_size);
 uint32_t savedPalette[512];std::memcpy(savedPalette,g_st.clut,sizeof(savedPalette));
 for(const auto& texture:g_vk_textures){
  std::memcpy(g_st.clut,texture.palette,sizeof(texture.palette));
  gs_vk_decode_texture(texture.info,g_vk_texels.data()+texture.offset);
 }
 std::memcpy(g_st.clut,savedPalette,sizeof(savedPalette));
 work.resize(80*28*2);
 for(unsigned i=0;i<80*28;i++) {
  work[i*2]=uint32_t(work.size());work[i*2+1]=uint32_t(g_vk_bins[i].size());
  work.insert(work.end(),g_vk_bins[i].begin(),g_vk_bins[i].end());
 }
 push[0]=uint32_t(work.size());
 work.insert(work.end(),g_vk_spans.begin(),g_vk_spans.end());
 push[1]=uint32_t(work.size());
 for(unsigned y=0;y<32;y++)for(unsigned x=0;x<64;x++)work.push_back(g_page_ct32[y][x]);
 push[2]=uint32_t(work.size());
 for(unsigned y=0;y<64;y++)for(unsigned x=0;x<64;x++)work.push_back(g_page_z16s[y][x]);
 }
 const auto ranges=[](const bool* blocks){
  std::vector<rojvk::Range> result;
  for(unsigned i=0;i<16384;){if(!blocks[i]){++i;continue;}const unsigned first=i;while(i<16384&&blocks[i])++i;result.push_back({first*256,(i-first)*256});}
  return result;
 };
 const bool ok=useGPU&&g_vk_device.render(g_vram,g_vk_triangles.data(),g_vk_triangles.size()*sizeof(VkTriangle),g_vk_texels,work,push,ranges(g_vk_read),ranges(g_vk_written));
 if(ok){++g_vk_batches;g_vk_rendered+=g_vk_triangles.size();}
 std::vector<uint32_t> gpu;
 if(validate&&ok){gpu.assign(std::begin(g_vram),std::end(g_vram));std::memcpy(g_vram,initial.data(),sizeof(g_vram));}
 if(!ok||validate) {
  if(!ok)g_vk_cpu+=g_vk_triangles.size();
  const GsState saved=g_st;uint32_t savedProv[32];std::memcpy(savedProv,g_clut_prov,sizeof(savedProv));
  g_vk_replaying=true;
  for(const auto& draw:g_vk_fallback){g_st=draw.state;std::memcpy(g_clut_prov,draw.provenance,sizeof(draw.provenance));gs_draw_triangle(draw.ci,draw.a,draw.b,draw.c);}
  g_vk_replaying=false;g_st=saved;std::memcpy(g_clut_prov,savedProv,sizeof(savedProv));
 }
 if(validate&&ok)for(size_t i=0;i<gpu.size();i++)if(gpu[i]!=g_vram[i]){
  const auto& first=g_vk_fallback.front();
  std::fprintf(stderr,"  [Vulkan VALIDATE] batch=%llu tris=%zu word=%zX GPU=%08X CPU=%08X persp=%d rgbaA=%.9g,%.9g,%.9g,%.9g rgbaB=%.9g,%.9g,%.9g,%.9g rgbaC=%.9g,%.9g,%.9g,%.9g\n",(unsigned long long)g_vk_batches,g_vk_triangles.size(),i,gpu[i],g_vram[i],first.a.persp,first.a.r,first.a.g,first.a.b,first.a.a,first.b.r,first.b.g,first.b.b,first.b.a,first.c.r,first.c.g,first.c.b,first.c.a);
  const char* names[]={"vk-triangles.bin","vk-work.bin","vk-textures.bin","vk-initial.bin"};const void* data[]={g_vk_triangles.data(),work.data(),g_vk_texels.data(),initial.data()};size_t sizes[]={g_vk_triangles.size()*sizeof(VkTriangle),work.size()*4,g_vk_texels.size()*4,initial.size()*4};
  for(int f=0;f<4;f++){FILE* file=std::fopen(names[f],"wb");if(file){std::fwrite(data[f],1,sizes[f],file);std::fclose(file);}}
  std::exit(3);
 }
 g_vk_triangles.clear();g_vk_fallback.clear();g_vk_textures.clear();g_vk_texels.clear();g_vk_spans.clear();
 g_vk_estimated_ns=0;
 g_vk_texture_size=0;
 for(auto& bin:g_vk_bins)bin.clear();std::fill(std::begin(g_vk_written),std::end(g_vk_written),false);
 std::fill(std::begin(g_vk_read),std::end(g_vk_read),false);
}
void gs_vk_palette_barrier(uint32_t bp) {
 if(g_vk_replaying||g_vk_triangles.empty())return;
 for(unsigned i=0;i<16;i++)if(g_vk_written[(bp+i)&16383]){gs_vk_flush();break;}
}
static void gs_vk_texture_barrier(const TexInfo& t) {
 if(g_vk_triangles.empty())return;
 unsigned bw=8,bh=8;
 if(t.psm==0x02||t.psm==0x0a)bw=16;
 if(t.psm==0x13){bw=16;bh=16;}if(t.psm==0x14){bw=32;bh=16;}
 for(int y=0;y<t.h;y+=int(bh))for(int x=0;x<t.w;x+=int(bw)) {
  uint32_t byte;
  if(t.psm==0x13)byte=ix_t8(t.tbp,t.tbw,x,y);
  else if(t.psm==0x14)byte=ix_t4(t.tbp,t.tbw,x,y)>>1;
  else if(t.psm==0x02||t.psm==0x0a)byte=ix_ct16(t.tbp,t.tbw,x,y)*2;
  else byte=ix_ct32(t.tbp,t.tbw,x,y)*4;
  if(g_vk_written[(byte>>8)&16383]){gs_vk_flush();return;}
 }
}
static bool gs_vk_self_alias(const PixCfg& pc,int minx,int maxx,int miny,int maxy) {
 uint64_t blocks[256]{};
 const auto mark=[&](unsigned block){blocks[block>>6]|=1ull<<(block&63);};
 for(int y=miny&~7;y<=maxy;y+=8)for(int x=minx&~7;x<=maxx;x+=8){
  mark(ix_ct32(pc.fbp,pc.fbw,x,y)>>6);
  if(pc.zactive&&!pc.zmsk)mark(ix_z16s(pc.zbp,pc.fbw,x,y)>>7);
 }
 const auto& t=pc.ti;unsigned bw=8,bh=8;
 if(t.psm==2||t.psm==0x0a)bw=16;
 if(t.psm==0x13){bw=16;bh=16;}if(t.psm==0x14){bw=32;bh=16;}
 for(int y=0;y<t.h;y+=int(bh))for(int x=0;x<t.w;x+=int(bw)){
  unsigned byte=t.psm==0x13?ix_t8(t.tbp,t.tbw,x,y):t.psm==0x14?(ix_t4(t.tbp,t.tbw,x,y)>>1):
   t.psm==2||t.psm==0x0a?ix_ct16(t.tbp,t.tbw,x,y)*2:ix_ct32(t.tbp,t.tbw,x,y)*4;
  const unsigned block=(byte>>8)&16383;
  if(blocks[block>>6]&(1ull<<(block&63)))return true;
 }
 return false;
}
static bool gs_vk_enqueue(int ci,const GsVertex& a,const GsVertex& b,const GsVertex& c,const PixCfg& pc,int minx,int maxx,int miny,int maxy) {
 if(!gs_vk_requested()||!g_vk_device.ready())return false;
 VkPrepareProfile profile;
 const bool perspective=a.persp&&b.persp&&c.persp;
 const bool flat=!g_nofastsprite&&!perspective&&int64_t(maxx-minx+1)*(maxy-miny+1)>=1024&&
  a.r==b.r&&b.r==c.r&&a.g==b.g&&b.g==c.g&&a.b==b.b&&b.b==c.b&&a.a==b.a&&b.a==c.a&&(!pc.tme||FastRaster::tex_ok(pc.ti));
 auto supported=[](const TexInfo& t) {
  if(t.w>512||t.h>512||t.ui_vpair)return false;
  if(t.umode>=2&&(t.uminv<0||t.umaxv>=t.w||(t.umode==3&&(t.uminv|t.umaxv)>=t.w)))return false;
  if(t.vmode>=2&&(t.vminv<0||t.vmaxv>=t.h||(t.vmode==3&&(t.vminv|t.vmaxv)>=t.h)))return false;
  switch(t.psm){case 0:case 1:case 2:case 0x0a:case 0x13:case 0x14:case 0x1b:case 0x24:case 0x2c:return true;default:return false;}
 };
 if(pc.fpsm!=0||pc.fbw!=10||(pc.fbp!=0&&pc.fbp!=2240)||minx<0||maxx>=640||miny<0||maxy>=224||
  (pc.zactive&&(pc.zpsm!=0x3a||pc.zbp<4480||pc.zbp>15000))||
  (pc.tme&&!supported(pc.ti))||gs_edge_t_enabled()||gs_tex_uv_bias_u()!=0.0f){++g_vk_cpu;return false;}
 if(minx>maxx||miny>maxy)return true;
 if(pc.tme&&!flat){
  const auto& t=pc.ti;unsigned sy=5,sx=6,pw=t.tbw;
  if(t.psm==2||t.psm==0x0a)sy=6;
  if(t.psm==0x13||t.psm==0x14){sx=7;sy=t.psm==0x13?6:7;pw=std::max(1u,t.tbw>>1);}
  const uint64_t low=uint64_t(t.tbp)*256;
  const uint64_t high=(uint64_t(t.tbp>>5)+((t.h-1)>>sy)*pw+((t.w-1)>>sx)+1)*8192+(t.tbp&31u)*256;
  if((high>4u*1024*1024||(low<uint64_t(pc.fbp+2240)*256&&high>uint64_t(pc.fbp)*256)||
   (pc.zactive&&!pc.zmsk&&low<uint64_t(pc.zbp+1280)*256&&high>uint64_t(pc.zbp)*256))&&
   gs_vk_self_alias(pc,minx,maxx,miny,maxy)){++g_vk_cpu;return false;}
 }
 static const bool single=std::getenv("PS2_GS_VK_SINGLE")!=nullptr;
 if(!g_vk_triangles.empty()&&(single||g_vk_triangles.front().state[0]!=pc.fbp||g_vk_triangles.size()>=4096))gs_vk_flush();
 if(pc.tme)gs_vk_texture_barrier(pc.ti);
 uint32_t textureOffset=0;
 if(pc.tme) {
  const auto& context=g_st.ctx[ci];
  uint64_t palette=1469598103934665603ull;
  if(pc.ti.psm>=0x13)for(unsigned i=0;i<(pc.ti.psm==0x14||pc.ti.psm==0x24||pc.ti.psm==0x2c?16u:256u);i++){
   unsigned slot=pc.ti.psm==0x14||pc.ti.psm==0x24||pc.ti.psm==0x2c?(pc.ti.csa16+i)&511:i;
   palette=(palette^g_st.clut[slot])*1099511628211ull;
  }
  uint64_t key[5]{context.tex0,context.tex1,context.clamp,g_st.texa,palette};
  auto found=std::find_if(g_vk_textures.begin(),g_vk_textures.end(),[&](const VkTexture& t){return std::memcmp(t.key,key,sizeof(key))==0;});
  if(found!=g_vk_textures.end())textureOffset=found->offset;
  else {
   if(g_vk_texture_size+size_t(pc.ti.w)*pc.ti.h>4u*1024*1024)gs_vk_flush();
   textureOffset=uint32_t(g_vk_texture_size);VkTexture texture{};std::memcpy(texture.key,key,sizeof(key));texture.offset=textureOffset;texture.info=pc.ti;
   std::memcpy(texture.palette,g_st.clut,sizeof(texture.palette));g_vk_textures.push_back(texture);
   g_vk_texture_size+=size_t(pc.ti.w)*pc.ti.h;
  }
 }
 VkTriangle triangle{};
 const float area=(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x),inv=1.0f/area;
 const float s0=(b.y-c.y)*inv,s1=(c.y-a.y)*inv,s2=-(s0+s1);
 const float c0=(b.x*c.y-b.y*c.x)*inv,d0=(c.x-b.x)*inv,c1=(c.x*a.y-c.y*a.x)*inv,d1=(a.x-c.x)*inv;
 triangle.coeff[0]=s0;triangle.coeff[1]=s1;triangle.coeff[2]=c0;triangle.coeff[3]=d0;triangle.coeff[4]=c1;triangle.coeff[5]=d1;
 const GsVertex* vertices[]{&a,&b,&c};
 triangle.coeff[6]=(a.u*s0+b.u*s1)+c.u*s2;triangle.coeff[7]=(a.v*s0+b.v*s1)+c.v*s2;
 triangle.z[3]=(float(a.z)*s0+float(b.z)*s1)+float(c.z)*s2;
 for(unsigned i=0;i<3;i++){
  const auto& v=*vertices[i];float rgba[]{v.r,v.g,v.b,v.a};std::memcpy(triangle.rgba+i*4,rgba,sizeof(rgba));
  triangle.uvq[i*4]=perspective?v.us:v.u;triangle.uvq[i*4+1]=perspective?v.vs:v.v;triangle.uvq[i*4+2]=perspective?v.q:1.0f;triangle.z[i]=float(v.z);
 }
 triangle.tex[0]=textureOffset;triangle.tex[1]=pc.ti.w;triangle.tex[2]=pc.ti.h;
 triangle.tex[3]=(pc.tme?1u:0u)|(pc.ti.linear?2u:0u)|(perspective?4u:0u)|(pc.ti.tcc?8u:0u)|(pc.ti.tfx<<4)|(flat?128u:0u);
 int32_t bounds[]{minx,maxx,miny,maxy};std::memcpy(triangle.bounds,bounds,sizeof(bounds));
 uint32_t state[]{pc.fbp,pc.fbw,pc.zbp,(pc.zactive?1u:0u)|(pc.zmsk?2u:0u)|(pc.ztst<<2)|(pc.abe?16u:0u)|(pc.clamp?32u:0u)};std::memcpy(triangle.state,state,sizeof(state));
 uint32_t alpha[]{unsigned(pc.ate),pc.atst,unsigned(pc.aref),pc.afail};std::memcpy(triangle.alpha,alpha,sizeof(alpha));
 uint32_t blend[]{pc.A|(pc.B<<2)|(pc.C<<4)|(pc.D<<6),unsigned(pc.fix),pc.fbmsk,0};std::memcpy(triangle.blend,blend,sizeof(blend));
 triangle.wrap[0]=pc.ti.umode;triangle.wrap[1]=pc.ti.vmode;
 int32_t region[]{pc.ti.uminv,pc.ti.umaxv,pc.ti.vminv,pc.ti.vmaxv};std::memcpy(triangle.region,region,sizeof(region));
 triangle.span[0]=uint32_t(g_vk_spans.size()/8);
 for(int y=miny;y<=maxy;y++){
  const float py=float(y)+0.5f,k0=c0+d0*py,k1=c1+d1*py,k2=1.0f-k0-k1;
  const float ks[]{k0,k1,k2},ss[]{s0,s1,s2};float lo=-1e30f,hi=1e30f;bool empty=false;
  for(int i=0;i<3;i++){if(ss[i]>1e-9f)lo=std::max(lo,-ks[i]/ss[i]);else if(ss[i]<-1e-9f)hi=std::min(hi,-ks[i]/ss[i]);else if(ks[i]<0)empty=true;}
  int xs=empty||lo>hi?1:std::max(minx,int(fast_ceil(lo-0.5f-1e-4f))),xe=empty||lo>hi?0:std::min(maxx,int(fast_floor(hi-0.5f+1e-4f)));
  if(xs<=xe)g_vk_estimated_ns+=uint64_t(xe-xs+1)*(flat?8u:(pc.tme?(pc.ti.linear?70u:40u):20u));
  g_vk_spans.push_back(uint32_t(xs));g_vk_spans.push_back(uint32_t(xe));
  float px=float(xs)+.5f,w0=k0+s0*px,w1=k1+s1*px,w2=1.0f-w0-w1;
  float values[]{(a.u*w0+b.u*w1)+c.u*w2,(a.v*w0+b.v*w1)+c.v*w2,(float(a.z)*w0+float(b.z)*w1)+float(c.z)*w2};
  for(float value:values){uint32_t bits;std::memcpy(&bits,&value,4);g_vk_spans.push_back(bits);}
  g_vk_spans.insert(g_vk_spans.end(),3,0u);
 }
 const unsigned index=unsigned(g_vk_triangles.size());g_vk_triangles.push_back(triangle);
 VkFallback saved{};saved.ci=ci;saved.a=a;saved.b=b;saved.c=c;saved.state=g_st;std::memcpy(saved.provenance,g_clut_prov,sizeof(saved.provenance));g_vk_fallback.push_back(std::move(saved));
 for(int ty=miny/8;ty<=maxy/8;ty++)for(int tx=minx/8;tx<=maxx/8;tx++)g_vk_bins[ty*80+tx].push_back(index);
 for(int y=miny&~7;y<=maxy;y+=8)for(int x=minx&~7;x<=maxx;x+=8){
  const unsigned colorBlock=ix_ct32(pc.fbp,pc.fbw,x,y)>>6;
  g_vk_written[colorBlock]=true;g_vk_read[colorBlock]=true;
  if(pc.zactive){const unsigned depthBlock=ix_z16s(pc.zbp,pc.fbw,x,y)>>7;g_vk_read[depthBlock]=true;if(!pc.zmsk)g_vk_written[depthBlock]=true;}
 }
 return true;
}
#else
void gs_vk_flush(){}
void gs_vk_palette_barrier(uint32_t){}
static bool gs_vk_enqueue(int,const GsVertex&,const GsVertex&,const GsVertex&,const PixCfg&,int,int,int,int){return false;}
#endif
