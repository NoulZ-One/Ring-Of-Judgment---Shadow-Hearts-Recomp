#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <string>
#include <vector>

namespace roj {
struct EffectBankKey {uint64_t sequence=0,primary=0,auxiliary=0;};
inline uint64_t effect_fingerprint(const uint8_t* data,size_t size,size_t skip=0){
    if(!data||size<=skip)return 0;
    uint64_t hash=14695981039346656037ull;
    for(size_t i=skip;i<size;++i){hash^=data[i];hash*=1099511628211ull;}return hash;
}
inline std::string effect_hex(uint64_t n){char text[17];std::snprintf(text,sizeof text,"%016llx",static_cast<unsigned long long>(n));return text;}
class ExternalEffects {
    struct Entry {std::string file;uint64_t begin,end;double gain;bool silent,approved;};
    struct Clip {std::vector<int16_t> pcm;uint64_t frames;};
    struct Cached {std::shared_ptr<const Clip> clip;uint64_t used;};
    struct Voice {std::shared_ptr<const Clip> clip;unsigned id;uint64_t position,begin,end;bool loop;double left,right;};
    std::filesystem::path root;
    std::map<std::string,std::string> banks,primaryBanks,sequenceBanks;
    std::map<std::string,Entry> entries;
    std::map<std::string,Cached> cache;
    std::set<std::string> failed;
    std::vector<Voice> voices;
    uint64_t clock=0;size_t cacheBytes=0;
    bool enabled=false;
    static constexpr size_t CacheLimit=16u<<20;
    static uint32_t word(const uint8_t* p,unsigned n){uint32_t v=0;for(unsigned i=0;i<n;++i)v|=uint32_t(p[i])<<(8*i);return v;}
    static bool slug(const std::string& s){return !s.empty()&&s.size()<100&&std::all_of(s.begin(),s.end(),[](unsigned char c){return(c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-';});}
    static void unique(std::map<std::string,std::string>& map,const std::string& key,const std::string& value){auto p=map.emplace(key,value);if(!p.second&&p.first->second!=value)p.first->second.clear();}
    std::string resolve(const EffectBankKey& key) const {
        const auto s=effect_hex(key.sequence),p=effect_hex(key.primary),a=effect_hex(key.auxiliary);
        auto exact=banks.find(s+"/"+p+"/"+a);if(exact!=banks.end())return exact->second;
        auto primary=primaryBanks.find(s+"/"+p);if(key.primary&&primary!=primaryBanks.end()&&!primary->second.empty())return primary->second;
        auto sequence=sequenceBanks.find(s);return sequence!=sequenceBanks.end()?sequence->second:std::string{};
    }
    std::shared_ptr<const Clip> load(const Entry& entry){
        auto old=cache.find(entry.file);if(old!=cache.end()){old->second.used=++clock;return old->second.clip;}
        if(failed.count(entry.file))return {};
        auto reject=[&]()->std::shared_ptr<const Clip>{failed.insert(entry.file);std::fprintf(stderr,"  [efeito externo] arquivo ausente/invalido: %s; usando RSSD\n",entry.file.c_str());return {};};
        std::ifstream file(root/std::filesystem::u8path(entry.file),std::ios::binary);if(!file)return reject();
        file.seekg(0,std::ios::end);const auto size=file.tellg();file.seekg(0);
        if(size<44||size>16*1024*1024)return reject();
        std::vector<uint8_t> bytes(static_cast<size_t>(size));if(!file.read(reinterpret_cast<char*>(bytes.data()),bytes.size()))return reject();
        if(word(bytes.data(),4)!=0x46464952||word(bytes.data()+8,4)!=0x45564157||word(bytes.data()+4,4)+8ull!=bytes.size())return reject();
        bool format=false;const uint8_t* data=nullptr;unsigned length=0;
        for(size_t at=12;at+8<=bytes.size();){
            const unsigned tag=word(bytes.data()+at,4),n=word(bytes.data()+at+4,4);at+=8;if(n>bytes.size()-at)return reject();
            const auto* p=bytes.data()+at;
            if(tag==0x20746d66)format=n>=16&&word(p,2)==1&&word(p+2,2)==2&&word(p+4,4)==22050&&word(p+8,4)==88200&&word(p+12,2)==4&&word(p+14,2)==16;
            if(tag==0x61746164){data=p;length=n;}at+=n+(n&1);
        }
        if(!format||!data||!length||length%4)return reject();
        auto clip=std::make_shared<Clip>();clip->frames=length/4;
        if(entry.end && (entry.begin>=entry.end||entry.end>clip->frames||entry.end-entry.begin<128))return reject();
        clip->pcm.resize(length/2);for(unsigned i=0;i<length/2;++i)clip->pcm[i]=int16_t(word(data+2*i,2));
        while(cacheBytes+length>CacheLimit&&!cache.empty()){
            auto oldest=std::min_element(cache.begin(),cache.end(),[](const auto& a,const auto& b){return a.second.used<b.second.used;});
            cacheBytes-=oldest->second.clip->pcm.size()*2;cache.erase(oldest);
        }
        cache.emplace(entry.file,Cached{clip,++clock});cacheBytes+=length;return clip;
    }
public:
    bool configure(const std::filesystem::path& directory){
        enabled=false;root=directory;banks.clear();primaryBanks.clear();sequenceBanks.clear();entries.clear();cache.clear();voices.clear();failed.clear();cacheBytes=0;
        std::ifstream mapping(root/"banks.tsv"),index(root/"effects.tsv");if(!mapping||!index)return false;
        std::string line;while(std::getline(mapping,line)){
            if(line.empty()||line[0]=='#')continue;
            std::istringstream row(line);std::string s,p,a,name;if(!(row>>s>>p>>a>>name)||!slug(name)||s.size()!=16||p.size()!=16||a.size()!=16)return false;
            auto added=banks.emplace(s+"/"+p+"/"+a,name);if(!added.second&&added.first->second!=name)return false;
            unique(primaryBanks,s+"/"+p,name);unique(sequenceBanks,s,name);
        }
        while(std::getline(index,line)){
            if(line.empty()||line[0]=='#')continue;
            std::istringstream row(line);std::string name,hex,file;uint64_t begin,end;double gain;unsigned silent,approved;
            if(!(row>>name>>hex>>file>>begin>>end>>gain>>silent>>approved)||!slug(name)||hex.size()!=3||!std::all_of(hex.begin(),hex.end(),[](char c){return(c>='0'&&c<='9')||(c>='A'&&c<='F');})||file!=name+"/"+hex+".wav"||!std::isfinite(gain)||gain<0||gain>2||silent>1||approved>1)return false;
            if(!entries.emplace(name+"/"+hex,Entry{file,begin,end,gain,bool(silent),bool(approved)}).second)return false;
        }
        enabled=!banks.empty()&&!entries.empty();return enabled;
    }
    bool play(const EffectBankKey& key,unsigned id,unsigned volume,unsigned pan,bool approvedOnly=false){
        if(!enabled)return false;
        const auto bank=resolve(key);if(bank.empty())return false;char hex[8];std::snprintf(hex,sizeof hex,"%03X",id&0xffffu);
        auto found=entries.find(bank+"/"+hex);if(found==entries.end()||(approvedOnly&&!found->second.approved))return false;
        const auto& e=found->second;if(e.silent||!volume)return true;
        auto clip=load(e);if(!clip)return false;
        if(e.end)stop(id,false);
        if(voices.size()>=32)voices.erase(voices.begin());
        const double gain=e.gain*std::min(volume,127u)/127.0;pan=std::min(pan,127u);
        voices.push_back({clip,id,0,e.begin,e.end?e.end:clip->frames,bool(e.end),gain*std::min(1.0,(127-pan)/63.0),gain*std::min(1.0,pan/64.0)});
        return true;
    }
    void stop(unsigned id,bool bank){voices.erase(std::remove_if(voices.begin(),voices.end(),[=](const Voice& v){return (bank?v.id>>16:v.id)==id;}),voices.end());}
    void render(int16_t* pcm,unsigned frames,double gain){
        for(unsigned frame=0;frame<frames&&!voices.empty();++frame){double left=pcm[2*frame],right=pcm[2*frame+1];
            for(auto v=voices.begin();v!=voices.end();){
                if(v->position==v->end){if(v->loop)v->position=v->begin;else{v=voices.erase(v);continue;}}
                double level=gain;if(v->loop){if(v->end-v->position<=64)level*=double(v->end-v->position-1)/64;if(v->position>=v->begin&&v->position-v->begin<64)level*=double(v->position-v->begin)/64;}
                left+=v->clip->pcm[v->position*2]*v->left*level;right+=v->clip->pcm[v->position*2+1]*v->right*level;++v->position;++v;
            }
            pcm[2*frame]=int16_t(std::clamp(std::lround(left),-32768l,32767l));pcm[2*frame+1]=int16_t(std::clamp(std::lround(right),-32768l,32767l));
        }
    }
    size_t active() const{return voices.size();}
    size_t cached_bytes() const{return cacheBytes;}
};
}
