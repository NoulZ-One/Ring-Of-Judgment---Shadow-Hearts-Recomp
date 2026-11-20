#pragma once
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <cstdint>
#include <algorithm>
#include <cmath>
#include <limits>

namespace roj {
// PCM is prepared offline from the supplied MP3s. Playback reads small blocks;
// no codec or complete-song allocation runs on the EE thread.
class ExternalMusic {
    std::ifstream file;
    uint64_t dataOffset=0, frames=0, position=0, begin=0, end=0;
    double gain=1;
    bool looping=false, failed=false;
    static uint32_t word(const unsigned char* b,unsigned n){uint32_t x=0;for(unsigned i=0;i<n;++i)x|=uint32_t(b[i])<<(i*8);return x;}
public:
    static std::string normalize(std::string id){
        std::string result;
        for(unsigned char c:id)if(c!='_')result+=char(c>='A'&&c<='Z'?c+32:c);
        if(result.size()!=6||result.compare(0,3,"bgm")||!std::all_of(result.begin()+3,result.end(),[](char c){return c>='0'&&c<='9';}))return {};
        return result;
    }
    bool open(const std::filesystem::path& root,const std::string& name){
        if(file.is_open())file.close();file.clear();
        dataOffset=frames=position=begin=end=0;gain=1;looping=failed=false;
        const auto id=normalize(name);if(id.empty())return false;
        std::ifstream manifest(root/"music.tsv");std::string line,leaf;int64_t loopStart=-1;uint64_t loopEnd=0;bool found=false;
        while(std::getline(manifest,line)){
            if(line.empty()||line[0]=='#')continue;
            std::istringstream row(line);std::string key,candidate;int64_t start;uint64_t stop;double volume;
            if(!(row>>key>>candidate>>start>>stop>>volume)||normalize(key)!=id)continue;
            if(candidate!=id+".wav"||!std::isfinite(volume)||volume<0||volume>2||start< -1)return false;
            leaf=candidate;loopStart=start;loopEnd=stop;gain=volume;found=true;break;
        }
        if(!found)return false;
        file.open(root/leaf,std::ios::binary);if(!file)return false;
        file.seekg(0,std::ios::end);const auto size=file.tellg();file.seekg(0);
        unsigned char header[12];if(!file.read(reinterpret_cast<char*>(header),12)||word(header,4)!=0x46464952||word(header+8,4)!=0x45564157||uint64_t(word(header+4,4))+8!=uint64_t(size))return false;
        bool format=false;uint64_t bytes=0;
        for(uint64_t at=12;at+8<=uint64_t(size);){
            unsigned char chunk[8];file.seekg(at);if(!file.read(reinterpret_cast<char*>(chunk),8))return false;
            const uint64_t length=word(chunk+4,4);if(length>uint64_t(size)-at-8)return false;
            if(word(chunk,4)==0x20746d66){
                unsigned char fmt[16];if(length<16||!file.read(reinterpret_cast<char*>(fmt),16))return false;
                format=word(fmt,2)==1&&word(fmt+2,2)==2&&word(fmt+4,4)==22050&&word(fmt+8,4)==88200&&word(fmt+12,2)==4&&word(fmt+14,2)==16;
            }else if(word(chunk,4)==0x61746164){dataOffset=at+8;bytes=length;}
            at+=8+length+(length&1);
        }
        if(!format||!bytes||bytes%4)return false;
        frames=bytes/4;looping=loopStart>=0;begin=looping?uint64_t(loopStart):0;end=looping?loopEnd:frames;
        if(!end||end>frames||(looping&&(begin>=end||end-begin<128)))return false;
        position=0;failed=false;file.clear();file.seekg(dataOffset);return bool(file);
    }
    bool render(int16_t* pcm,unsigned count){
        std::fill_n(pcm,count*2,int16_t(0));if(failed)return false;
        unsigned done=0;
        while(done<count){
            if(position==end){if(!looping)break;position=begin;file.clear();file.seekg(dataOffset+position*4);}
            const unsigned n=unsigned(std::min<uint64_t>(count-done,end-position));
            if(!file.read(reinterpret_cast<char*>(pcm+done*2),n*4)){failed=true;std::fill_n(pcm+done*2,(count-done)*2,int16_t(0));return false;}
            for(unsigned i=0;i<n;++i){
                double level=gain;
                // A short boundary fade avoids a click without changing the
                // period or moving the original loop markers.
                if(looping){const uint64_t at=position+i;
                    if(end-at<=64)level*=double(end-at-1)/64;
                    if(at>=begin&&at-begin<64)level*=double(at-begin)/64;
                }
                for(unsigned channel=0;channel<2;++channel){auto& value=pcm[(done+i)*2+channel];value=int16_t(std::clamp(std::lround(value*level),-32768l,32767l));}
            }
            position+=n;done+=n;
        }
        return true;
    }
};
}
