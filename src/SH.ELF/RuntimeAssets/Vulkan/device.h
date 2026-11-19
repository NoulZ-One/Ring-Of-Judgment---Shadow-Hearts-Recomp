// Loaded dynamically: the game still starts when vulkan-1.dll is unavailable.
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <stdexcept>
#include <chrono>
namespace rojvk {
struct Range { uint32_t offset,size; };
struct Buffer { VkBuffer buffer{}; VkDeviceMemory memory{}; void* mapped{}; VkDeviceSize size{}; bool coherent{}; };
class Device {
 HMODULE library{};
 VkInstance instance{}; VkPhysicalDevice physical{}; VkDevice device{}; VkQueue queue{};
 VkDescriptorSetLayout layout{}; VkDescriptorPool descriptors{}; VkDescriptorSet set{};
 VkPipelineLayout pipelineLayout{}; VkPipeline pipeline{}; VkCommandPool pool{};
 VkCommandBuffer command{}; VkFence fence{};
 Buffer buffers[4]{};
 Buffer gpuBuffers[4]{};
 bool attempted=false,available=false;
 PFN_vkGetInstanceProcAddr getInstance{}; PFN_vkGetDeviceProcAddr getDevice{};
#define ROJ_VK_FUNCTION(name) PFN_##name name{};
 ROJ_VK_FUNCTION(vkCreateInstance) ROJ_VK_FUNCTION(vkDestroyInstance)
 ROJ_VK_FUNCTION(vkEnumeratePhysicalDevices) ROJ_VK_FUNCTION(vkGetPhysicalDeviceProperties)
 ROJ_VK_FUNCTION(vkGetPhysicalDeviceQueueFamilyProperties) ROJ_VK_FUNCTION(vkGetPhysicalDeviceMemoryProperties)
 ROJ_VK_FUNCTION(vkCreateDevice) ROJ_VK_FUNCTION(vkDestroyDevice) ROJ_VK_FUNCTION(vkGetDeviceQueue)
 ROJ_VK_FUNCTION(vkCreateBuffer) ROJ_VK_FUNCTION(vkGetBufferMemoryRequirements)
 ROJ_VK_FUNCTION(vkAllocateMemory) ROJ_VK_FUNCTION(vkBindBufferMemory) ROJ_VK_FUNCTION(vkMapMemory)
 ROJ_VK_FUNCTION(vkUnmapMemory) ROJ_VK_FUNCTION(vkDestroyBuffer) ROJ_VK_FUNCTION(vkFreeMemory)
 ROJ_VK_FUNCTION(vkFlushMappedMemoryRanges) ROJ_VK_FUNCTION(vkInvalidateMappedMemoryRanges)
 ROJ_VK_FUNCTION(vkCreateDescriptorSetLayout) ROJ_VK_FUNCTION(vkDestroyDescriptorSetLayout)
 ROJ_VK_FUNCTION(vkCreateDescriptorPool) ROJ_VK_FUNCTION(vkDestroyDescriptorPool)
 ROJ_VK_FUNCTION(vkAllocateDescriptorSets) ROJ_VK_FUNCTION(vkUpdateDescriptorSets)
 ROJ_VK_FUNCTION(vkCreatePipelineLayout) ROJ_VK_FUNCTION(vkDestroyPipelineLayout)
 ROJ_VK_FUNCTION(vkCreateShaderModule) ROJ_VK_FUNCTION(vkDestroyShaderModule)
 ROJ_VK_FUNCTION(vkCreateComputePipelines) ROJ_VK_FUNCTION(vkDestroyPipeline)
 ROJ_VK_FUNCTION(vkCreateCommandPool) ROJ_VK_FUNCTION(vkDestroyCommandPool)
 ROJ_VK_FUNCTION(vkAllocateCommandBuffers) ROJ_VK_FUNCTION(vkResetCommandBuffer)
 ROJ_VK_FUNCTION(vkBeginCommandBuffer) ROJ_VK_FUNCTION(vkEndCommandBuffer)
 ROJ_VK_FUNCTION(vkCmdPipelineBarrier) ROJ_VK_FUNCTION(vkCmdBindPipeline)
 ROJ_VK_FUNCTION(vkCmdBindDescriptorSets) ROJ_VK_FUNCTION(vkCmdPushConstants) ROJ_VK_FUNCTION(vkCmdDispatch)
 ROJ_VK_FUNCTION(vkCmdCopyBuffer)
 ROJ_VK_FUNCTION(vkCreateFence) ROJ_VK_FUNCTION(vkDestroyFence) ROJ_VK_FUNCTION(vkResetFences)
 ROJ_VK_FUNCTION(vkQueueSubmit) ROJ_VK_FUNCTION(vkWaitForFences) ROJ_VK_FUNCTION(vkDeviceWaitIdle)
#undef ROJ_VK_FUNCTION
 static void check(VkResult result,const char* operation) {
  if(result!=VK_SUCCESS)throw std::runtime_error(std::string(operation)+" (VkResult="+std::to_string(result)+")");
 }
 void createBuffer(Buffer& b,VkDeviceSize size,bool gpuLocal=false) {
  b.size=size;
  VkBufferCreateInfo info{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};info.size=size;info.usage=VK_BUFFER_USAGE_STORAGE_BUFFER_BIT|VK_BUFFER_USAGE_TRANSFER_SRC_BIT|VK_BUFFER_USAGE_TRANSFER_DST_BIT;info.sharingMode=VK_SHARING_MODE_EXCLUSIVE;
  check(vkCreateBuffer(device,&info,nullptr,&b.buffer),"vkCreateBuffer");
  VkMemoryRequirements requirements{};vkGetBufferMemoryRequirements(device,b.buffer,&requirements);
  VkPhysicalDeviceMemoryProperties memory{};vkGetPhysicalDeviceMemoryProperties(physical,&memory);
  uint32_t type=UINT32_MAX;int best=-1;
  for(uint32_t i=0;i<memory.memoryTypeCount;i++) {
   auto flags=memory.memoryTypes[i].propertyFlags;
   if(!(requirements.memoryTypeBits&(1u<<i))||!(flags&(gpuLocal?VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT:VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)))continue;
   int score=((flags&VK_MEMORY_PROPERTY_HOST_CACHED_BIT)?4:0)+((flags&VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)?2:0)+((flags&VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)?1:0);
   if(score>best){best=score;type=i;}
  }
  if(type==UINT32_MAX)throw std::runtime_error("memoria Vulkan visivel ao host indisponivel");
  b.coherent=(memory.memoryTypes[type].propertyFlags&VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)!=0;
  VkMemoryAllocateInfo allocation{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};allocation.allocationSize=requirements.size;allocation.memoryTypeIndex=type;
  check(vkAllocateMemory(device,&allocation,nullptr,&b.memory),"vkAllocateMemory");
  check(vkBindBufferMemory(device,b.buffer,b.memory,0),"vkBindBufferMemory");
  if(!gpuLocal)check(vkMapMemory(device,b.memory,0,VK_WHOLE_SIZE,0,&b.mapped),"vkMapMemory");
 }
 void initialize() {
  library=LoadLibraryW(L"vulkan-1.dll");if(!library)throw std::runtime_error("vulkan-1.dll indisponivel");
  getInstance=reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(library,"vkGetInstanceProcAddr"));
  if(!getInstance)throw std::runtime_error("loader Vulkan incompleto");
  vkCreateInstance=reinterpret_cast<PFN_vkCreateInstance>(getInstance(nullptr,"vkCreateInstance"));
  VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};app.pApplicationName="Ring Of Judgment";app.apiVersion=VK_API_VERSION_1_1;
  VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};info.pApplicationInfo=&app;
  check(vkCreateInstance(&info,nullptr,&instance),"vkCreateInstance");
#define ROJ_VK_INSTANCE(name) name=reinterpret_cast<PFN_##name>(getInstance(instance,#name));if(!name)throw std::runtime_error(#name);
  ROJ_VK_INSTANCE(vkDestroyInstance) ROJ_VK_INSTANCE(vkEnumeratePhysicalDevices)
  ROJ_VK_INSTANCE(vkGetPhysicalDeviceProperties) ROJ_VK_INSTANCE(vkGetPhysicalDeviceQueueFamilyProperties)
  ROJ_VK_INSTANCE(vkGetPhysicalDeviceMemoryProperties) ROJ_VK_INSTANCE(vkCreateDevice)
#undef ROJ_VK_INSTANCE
  getDevice=reinterpret_cast<PFN_vkGetDeviceProcAddr>(getInstance(instance,"vkGetDeviceProcAddr"));
  uint32_t count=0;check(vkEnumeratePhysicalDevices(instance,&count,nullptr),"vkEnumeratePhysicalDevices");
  std::vector<VkPhysicalDevice> devices(count);check(vkEnumeratePhysicalDevices(instance,&count,devices.data()),"vkEnumeratePhysicalDevices");
  uint32_t family=0;int best=-1;VkPhysicalDeviceProperties selected{};
  for(auto candidate:devices) {
   VkPhysicalDeviceProperties props{};vkGetPhysicalDeviceProperties(candidate,&props);
   if(props.deviceType==VK_PHYSICAL_DEVICE_TYPE_CPU)continue;
   uint32_t n=0;vkGetPhysicalDeviceQueueFamilyProperties(candidate,&n,nullptr);std::vector<VkQueueFamilyProperties> families(n);vkGetPhysicalDeviceQueueFamilyProperties(candidate,&n,families.data());
   for(uint32_t j=0;j<n;j++)if(families[j].queueCount&&(families[j].queueFlags&VK_QUEUE_COMPUTE_BIT)){
    int score=props.deviceType==VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU?2:1;
    if(score>best){best=score;physical=candidate;family=j;selected=props;}break;
   }
  }
  if(!physical)throw std::runtime_error("nenhuma GPU Vulkan de computacao disponivel");
  float priority=1;VkDeviceQueueCreateInfo qi{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};qi.queueFamilyIndex=family;qi.queueCount=1;qi.pQueuePriorities=&priority;
  VkDeviceCreateInfo di{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};di.queueCreateInfoCount=1;di.pQueueCreateInfos=&qi;
  check(vkCreateDevice(physical,&di,nullptr,&device),"vkCreateDevice");
#define ROJ_VK_DEVICE(name) name=reinterpret_cast<PFN_##name>(getDevice(device,#name));if(!name)throw std::runtime_error(#name);
  ROJ_VK_DEVICE(vkDestroyDevice) ROJ_VK_DEVICE(vkGetDeviceQueue)
  ROJ_VK_DEVICE(vkCreateBuffer) ROJ_VK_DEVICE(vkGetBufferMemoryRequirements) ROJ_VK_DEVICE(vkAllocateMemory) ROJ_VK_DEVICE(vkBindBufferMemory) ROJ_VK_DEVICE(vkMapMemory)
  ROJ_VK_DEVICE(vkUnmapMemory) ROJ_VK_DEVICE(vkDestroyBuffer) ROJ_VK_DEVICE(vkFreeMemory) ROJ_VK_DEVICE(vkFlushMappedMemoryRanges) ROJ_VK_DEVICE(vkInvalidateMappedMemoryRanges)
  ROJ_VK_DEVICE(vkCreateDescriptorSetLayout) ROJ_VK_DEVICE(vkDestroyDescriptorSetLayout) ROJ_VK_DEVICE(vkCreateDescriptorPool) ROJ_VK_DEVICE(vkDestroyDescriptorPool) ROJ_VK_DEVICE(vkAllocateDescriptorSets) ROJ_VK_DEVICE(vkUpdateDescriptorSets)
  ROJ_VK_DEVICE(vkCreatePipelineLayout) ROJ_VK_DEVICE(vkDestroyPipelineLayout) ROJ_VK_DEVICE(vkCreateShaderModule) ROJ_VK_DEVICE(vkDestroyShaderModule) ROJ_VK_DEVICE(vkCreateComputePipelines) ROJ_VK_DEVICE(vkDestroyPipeline)
  ROJ_VK_DEVICE(vkCreateCommandPool) ROJ_VK_DEVICE(vkDestroyCommandPool) ROJ_VK_DEVICE(vkAllocateCommandBuffers) ROJ_VK_DEVICE(vkResetCommandBuffer) ROJ_VK_DEVICE(vkBeginCommandBuffer) ROJ_VK_DEVICE(vkEndCommandBuffer)
  ROJ_VK_DEVICE(vkCmdPipelineBarrier) ROJ_VK_DEVICE(vkCmdBindPipeline) ROJ_VK_DEVICE(vkCmdBindDescriptorSets) ROJ_VK_DEVICE(vkCmdPushConstants) ROJ_VK_DEVICE(vkCmdDispatch)
  ROJ_VK_DEVICE(vkCmdCopyBuffer)
  ROJ_VK_DEVICE(vkCreateFence) ROJ_VK_DEVICE(vkDestroyFence) ROJ_VK_DEVICE(vkResetFences) ROJ_VK_DEVICE(vkQueueSubmit) ROJ_VK_DEVICE(vkWaitForFences) ROJ_VK_DEVICE(vkDeviceWaitIdle)
#undef ROJ_VK_DEVICE
  vkGetDeviceQueue(device,family,0,&queue);
  createBuffer(buffers[0],4u*1024*1024);createBuffer(buffers[1],4096u*272);createBuffer(buffers[2],16u*1024*1024);createBuffer(buffers[3],32u*1024*1024);
  for(unsigned i=0;i<4;i++)createBuffer(gpuBuffers[i],buffers[i].size,true);
  VkDescriptorSetLayoutBinding bindings[4]{};for(uint32_t i=0;i<4;i++)bindings[i]={i,VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,1,VK_SHADER_STAGE_COMPUTE_BIT,nullptr};
  VkDescriptorSetLayoutCreateInfo li{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};li.bindingCount=4;li.pBindings=bindings;check(vkCreateDescriptorSetLayout(device,&li,nullptr,&layout),"vkCreateDescriptorSetLayout");
  VkDescriptorPoolSize ps{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,4};VkDescriptorPoolCreateInfo pi{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};pi.maxSets=1;pi.poolSizeCount=1;pi.pPoolSizes=&ps;check(vkCreateDescriptorPool(device,&pi,nullptr,&descriptors),"vkCreateDescriptorPool");
  VkDescriptorSetAllocateInfo ai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};ai.descriptorPool=descriptors;ai.descriptorSetCount=1;ai.pSetLayouts=&layout;check(vkAllocateDescriptorSets(device,&ai,&set),"vkAllocateDescriptorSets");
  VkDescriptorBufferInfo bi[4]{};VkWriteDescriptorSet writes[4]{};
  for(uint32_t i=0;i<4;i++){bi[i]={gpuBuffers[i].buffer,0,gpuBuffers[i].size};writes[i]={VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};writes[i].dstSet=set;writes[i].dstBinding=i;writes[i].descriptorCount=1;writes[i].descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;writes[i].pBufferInfo=&bi[i];}vkUpdateDescriptorSets(device,4,writes,0,nullptr);
  VkPushConstantRange range{VK_SHADER_STAGE_COMPUTE_BIT,0,16};VkPipelineLayoutCreateInfo pl{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};pl.setLayoutCount=1;pl.pSetLayouts=&layout;pl.pushConstantRangeCount=1;pl.pPushConstantRanges=&range;check(vkCreatePipelineLayout(device,&pl,nullptr,&pipelineLayout),"vkCreatePipelineLayout");
  VkShaderModuleCreateInfo si{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};si.codeSize=sizeof(roj_vk_shader);si.pCode=roj_vk_shader;VkShaderModule shader{};check(vkCreateShaderModule(device,&si,nullptr,&shader),"vkCreateShaderModule");
  VkComputePipelineCreateInfo ci{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};ci.layout=pipelineLayout;ci.stage={VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO};ci.stage.stage=VK_SHADER_STAGE_COMPUTE_BIT;ci.stage.module=shader;ci.stage.pName="main";
  VkResult pipelineResult=vkCreateComputePipelines(device,nullptr,1,&ci,nullptr,&pipeline);vkDestroyShaderModule(device,shader,nullptr);check(pipelineResult,"vkCreateComputePipelines");
  VkCommandPoolCreateInfo cp{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};cp.flags=VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;cp.queueFamilyIndex=family;check(vkCreateCommandPool(device,&cp,nullptr,&pool),"vkCreateCommandPool");
  VkCommandBufferAllocateInfo ca{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};ca.commandPool=pool;ca.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY;ca.commandBufferCount=1;check(vkAllocateCommandBuffers(device,&ca,&command),"vkAllocateCommandBuffers");
  VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};check(vkCreateFence(device,&fi,nullptr,&fence),"vkCreateFence");
  std::fprintf(stderr,"  [Vulkan] GPU: %s; rasterizacao de triangulos por compute\n",selected.deviceName);
 }
public:
 bool ready() {
  if(!attempted){attempted=true;try{initialize();available=true;}catch(const std::exception& e){std::fprintf(stderr,"  [Vulkan] indisponivel: %s; usando CPU\n",e.what());}}
  return available;
 }
 bool render(uint32_t* vram,const void* triangles,size_t triangleBytes,const std::vector<uint32_t>& texture,const std::vector<uint32_t>& work,const uint32_t* push,
             const std::vector<Range>& reads,const std::vector<Range>& writes) {
  if(!ready())return false;
  try {
   const bool profile=std::getenv("PS2_GS_VK_PROFILE")!=nullptr;
   auto clock=[] {return std::chrono::steady_clock::now();};
   const auto beginTime=clock();
   if(triangleBytes>buffers[1].size||texture.size()*4>buffers[2].size||work.size()*4>buffers[3].size)throw std::runtime_error("lote excedeu o buffer Vulkan");
   std::vector<VkBufferCopy> uploads,downloads;
   size_t uploadBytes=0,downloadBytes=0;
   for(const auto& range:reads){
    if(uint64_t(range.offset)+range.size>4u*1024*1024||(range.offset&3)||(range.size&3)||!range.size)throw std::runtime_error("faixa de leitura Vulkan invalida");
    uploads.push_back({range.offset,range.offset,range.size});uploadBytes+=range.size;
    std::memcpy(static_cast<uint8_t*>(buffers[0].mapped)+range.offset,reinterpret_cast<uint8_t*>(vram)+range.offset,range.size);
   }
   for(const auto& range:writes){
    if(uint64_t(range.offset)+range.size>4u*1024*1024||(range.offset&3)||(range.size&3)||!range.size)throw std::runtime_error("faixa de escrita Vulkan invalida");
    downloads.push_back({range.offset,range.offset,range.size});downloadBytes+=range.size;
   }
   std::memcpy(buffers[1].mapped,triangles,triangleBytes);
   if(!texture.empty())std::memcpy(buffers[2].mapped,texture.data(),texture.size()*4);
   std::memcpy(buffers[3].mapped,work.data(),work.size()*4);
   const auto copyTime=clock();
   for(auto& b:buffers)if(!b.coherent){VkMappedMemoryRange r{VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE};r.memory=b.memory;r.size=VK_WHOLE_SIZE;check(vkFlushMappedMemoryRanges(device,1,&r),"vkFlushMappedMemoryRanges");}
   check(vkResetCommandBuffer(command,0),"vkResetCommandBuffer");VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};begin.flags=VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;check(vkBeginCommandBuffer(command,&begin),"vkBeginCommandBuffer");
   VkMemoryBarrier host{VK_STRUCTURE_TYPE_MEMORY_BARRIER};host.srcAccessMask=VK_ACCESS_HOST_WRITE_BIT;host.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT;
   vkCmdPipelineBarrier(command,VK_PIPELINE_STAGE_HOST_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&host,0,nullptr,0,nullptr);
   VkDeviceSize sizes[]{4u*1024*1024,triangleBytes,texture.size()*4,work.size()*4};
   if(!uploads.empty())vkCmdCopyBuffer(command,buffers[0].buffer,gpuBuffers[0].buffer,uint32_t(uploads.size()),uploads.data());
   for(unsigned i=1;i<4;i++)if(sizes[i]){VkBufferCopy copy{0,0,sizes[i]};vkCmdCopyBuffer(command,buffers[i].buffer,gpuBuffers[i].buffer,1,&copy);}
   VkMemoryBarrier before{VK_STRUCTURE_TYPE_MEMORY_BARRIER};before.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;before.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;
   vkCmdPipelineBarrier(command,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&before,0,nullptr,0,nullptr);
   vkCmdBindPipeline(command,VK_PIPELINE_BIND_POINT_COMPUTE,pipeline);vkCmdBindDescriptorSets(command,VK_PIPELINE_BIND_POINT_COMPUTE,pipelineLayout,0,1,&set,0,nullptr);vkCmdPushConstants(command,pipelineLayout,VK_SHADER_STAGE_COMPUTE_BIT,0,16,push);vkCmdDispatch(command,80,28,1);
   VkMemoryBarrier after{VK_STRUCTURE_TYPE_MEMORY_BARRIER};after.srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;after.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT;vkCmdPipelineBarrier(command,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&after,0,nullptr,0,nullptr);
   if(!downloads.empty())vkCmdCopyBuffer(command,gpuBuffers[0].buffer,buffers[0].buffer,uint32_t(downloads.size()),downloads.data());
   VkMemoryBarrier finish{VK_STRUCTURE_TYPE_MEMORY_BARRIER};finish.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;finish.dstAccessMask=VK_ACCESS_HOST_READ_BIT;vkCmdPipelineBarrier(command,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_HOST_BIT,0,1,&finish,0,nullptr,0,nullptr);
   check(vkEndCommandBuffer(command),"vkEndCommandBuffer");check(vkResetFences(device,1,&fence),"vkResetFences");VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};submit.commandBufferCount=1;submit.pCommandBuffers=&command;check(vkQueueSubmit(queue,1,&submit,fence),"vkQueueSubmit");
   check(vkWaitForFences(device,1,&fence,VK_TRUE,5000000000ull),"vkWaitForFences");
   const auto waitTime=clock();
   if(!buffers[0].coherent){VkMappedMemoryRange r{VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE};r.memory=buffers[0].memory;r.size=VK_WHOLE_SIZE;check(vkInvalidateMappedMemoryRanges(device,1,&r),"vkInvalidateMappedMemoryRanges");}
   for(const auto& range:writes)std::memcpy(reinterpret_cast<uint8_t*>(vram)+range.offset,static_cast<uint8_t*>(buffers[0].mapped)+range.offset,range.size);
   if(profile)std::fprintf(stderr,"  [Vulkan PERF] uploadCPU=%.3f GPU_sync=%.3f downloadCPU=%.3f tris=%zu texels=%zu work=%zu uploadBytes=%zu downloadBytes=%zu regions=%zu/%zu\n",std::chrono::duration<double,std::milli>(copyTime-beginTime).count(),std::chrono::duration<double,std::milli>(waitTime-copyTime).count(),std::chrono::duration<double,std::milli>(clock()-waitTime).count(),triangleBytes/272,texture.size(),work.size(),uploadBytes,downloadBytes,uploads.size(),downloads.size());
   return true;
  }catch(const std::exception& e){available=false;std::fprintf(stderr,"  [Vulkan] falha: %s; lote refeito na CPU\n",e.what());return false;}
 }
 ~Device(){
  if(device&&vkDeviceWaitIdle)vkDeviceWaitIdle(device);
  if(device){
   if(fence&&vkDestroyFence)vkDestroyFence(device,fence,nullptr);if(pool&&vkDestroyCommandPool)vkDestroyCommandPool(device,pool,nullptr);if(pipeline&&vkDestroyPipeline)vkDestroyPipeline(device,pipeline,nullptr);if(pipelineLayout&&vkDestroyPipelineLayout)vkDestroyPipelineLayout(device,pipelineLayout,nullptr);
   if(descriptors&&vkDestroyDescriptorPool)vkDestroyDescriptorPool(device,descriptors,nullptr);if(layout&&vkDestroyDescriptorSetLayout)vkDestroyDescriptorSetLayout(device,layout,nullptr);
   for(auto& b:buffers){if(b.mapped&&vkUnmapMemory)vkUnmapMemory(device,b.memory);if(b.buffer&&vkDestroyBuffer)vkDestroyBuffer(device,b.buffer,nullptr);if(b.memory&&vkFreeMemory)vkFreeMemory(device,b.memory,nullptr);}
   for(auto& b:gpuBuffers){if(b.buffer&&vkDestroyBuffer)vkDestroyBuffer(device,b.buffer,nullptr);if(b.memory&&vkFreeMemory)vkFreeMemory(device,b.memory,nullptr);}
   if(vkDestroyDevice)vkDestroyDevice(device,nullptr);
  }
  if(instance&&vkDestroyInstance)vkDestroyInstance(instance,nullptr);if(library)FreeLibrary(library);
 }
};
}
#endif
