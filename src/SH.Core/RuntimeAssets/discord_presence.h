#pragma once
#include "roj_settings.h"
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>

// Local Discord IPC only. No SDK DLL, credentials, HTTP or gameplay hooks.
// Protocol: https://docs.discord.com/developers/topics/rpc
namespace roj {
inline Settings discord_settings() {
    wchar_t executable[32768];
    DWORD length=GetModuleFileNameW(nullptr,executable,32768);
    if(length&&length<32768)
        return read_settings(std::filesystem::path(executable).parent_path()/L"RingOfJudgment.ini");
    Settings disabled;disabled.discord_presence=false;return disabled;
}
class DiscordPresence {
    std::atomic<bool> stopped{false};
    std::mutex mutex;
    std::condition_variable wake;
    std::thread worker;
    std::string application;
    std::wstring prefix;
    std::chrono::milliseconds retry;
    const int64_t started = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    bool wait(std::chrono::milliseconds duration) {
        std::unique_lock<std::mutex> lock(mutex);
        return wake.wait_for(lock,duration,[&]{return stopped.load();});
    }
    bool transfer(HANDLE pipe,void* data,DWORD bytes,bool writing) {
        auto* next=static_cast<char*>(data);
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(2);
        while(bytes&&!stopped){
            OVERLAPPED operation{};operation.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
            if(!operation.hEvent)return false;
            DWORD done=0;
            BOOL ok=writing?WriteFile(pipe,next,bytes,&done,&operation):ReadFile(pipe,next,bytes,&done,&operation);
            if(!ok&&GetLastError()==ERROR_IO_PENDING){
                while(!stopped&&std::chrono::steady_clock::now()<deadline&&
                      WaitForSingleObject(operation.hEvent,50)==WAIT_TIMEOUT){}
                if(WaitForSingleObject(operation.hEvent,0)!=WAIT_OBJECT_0)CancelIoEx(pipe,&operation);
                ok=GetOverlappedResult(pipe,&operation,&done,TRUE);
            }
            CloseHandle(operation.hEvent);
            if(!ok||!done)return false;
            next+=done;bytes-=done;
        }
        return bytes==0;
    }
    bool send(HANDLE pipe,uint32_t opcode,const std::string& payload) {
        uint32_t header[]{opcode,uint32_t(payload.size())};
        return transfer(pipe,header,sizeof(header),true)&&
            transfer(pipe,const_cast<char*>(payload.data()),DWORD(payload.size()),true);
    }
    bool receive(HANDLE pipe,uint32_t& opcode,std::string& payload) {
        uint32_t header[2];
        if(!transfer(pipe,header,sizeof(header),false)||header[1]>65536)return false;
        opcode=header[0];payload.resize(header[1]);
        return transfer(pipe,payload.data(),header[1],false);
    }
    static bool event(const std::string& json,const char* name) {
        size_t p=json.find("\"evt\"");if(p==std::string::npos)return false;
        p=json.find(':',p+5);if(p==std::string::npos)return false;
        p=json.find_first_not_of(" \r\n\t",p+1);
        return p!=std::string::npos&&json.compare(p,std::strlen(name)+2,std::string("\"")+name+"\"")==0;
    }
    bool connected(HANDLE pipe) {
        if(!send(pipe,0,"{\"v\":1,\"client_id\":\""+application+"\"}"))return false;
        bool ready=false;uint32_t opcode;std::string payload;
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(5);
        while(!ready&&!stopped&&std::chrono::steady_clock::now()<deadline){
            DWORD available=0;if(!PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr))return false;
            if(available<8){wait(std::chrono::milliseconds(50));continue;}
            if(!receive(pipe,opcode,payload)||opcode==2)return false;
            if(opcode==3){if(!send(pipe,4,payload))return false;}
            else if(opcode==1){if(event(payload,"ERROR"))return false;ready=event(payload,"READY");}
        }
        if(!ready)return false;
        const std::string activity="{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":"+
            std::to_string(GetCurrentProcessId())+",\"activity\":{\"type\":0,\"details\":\"Ring Of Judgment - Shadow Hearts\","
            "\"timestamps\":{\"start\":"+std::to_string(started)+"},\"instance\":false}},\"nonce\":\"roj-presence\"}";
        if(!send(pipe,1,activity))return false;
        while(!stopped){
            DWORD available=0;if(!PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr))return false;
            if(available<8){wait(std::chrono::milliseconds(250));continue;}
            if(!receive(pipe,opcode,payload)||opcode==2)return false;
            if(opcode==3){if(!send(pipe,4,payload))return false;}
            else if(opcode==1&&event(payload,"ERROR"))return false;
        }
        return true;
    }
    void run() noexcept {
        try {
            while(!stopped){
                HANDLE pipe=INVALID_HANDLE_VALUE;
                for(unsigned n=0;n<10&&!stopped;n++){
                    pipe=CreateFileW((prefix+std::to_wstring(n)).c_str(),GENERIC_READ|GENERIC_WRITE,
                        0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
                    if(pipe!=INVALID_HANDLE_VALUE)break;
                }
                if(pipe!=INVALID_HANDLE_VALUE){
                    try{connected(pipe);}catch(...){CloseHandle(pipe);throw;}
                    CloseHandle(pipe); // The activity belongs to this connection/process.
                }
                if(!stopped)wait(retry);
            }
        }catch(...){ /* Presence failure must never terminate the game. */ }
    }
public:
    explicit DiscordPresence(const Settings& settings=discord_settings(),
        std::wstring pipePrefix=L"\\\\?\\pipe\\discord-ipc-",
        std::chrono::milliseconds retryDelay=std::chrono::seconds(5))
        :application(settings.discord_application_id),prefix(std::move(pipePrefix)),retry(retryDelay) {
        if(settings.discord_presence&&valid_discord_id(application)){
            try{worker=std::thread([this]{run();});}catch(...){}
        }
    }
    ~DiscordPresence(){stopped=true;wake.notify_all();if(worker.joinable())worker.join();}
    DiscordPresence(const DiscordPresence&)=delete;
    DiscordPresence& operator=(const DiscordPresence&)=delete;
};
}
#else
namespace roj {struct DiscordPresence {explicit DiscordPresence(const Settings& = Settings{}) {}};}
#endif
