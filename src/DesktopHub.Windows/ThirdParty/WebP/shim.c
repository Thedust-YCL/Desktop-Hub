/* DesktopHub's fixed, memory-bounded WebP first-frame ABI. libwebp BSD license: COPYING/PATENTS. */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "webp/decode.h"
#include "webp/demux.h"
#define EXPORT __declspec(dllexport)
#define BUDGET (16u * 1024u * 1024u)
#define INPUT_LIMIT BUDGET

static WebPDemuxer* open_webp(const uint8_t* bytes, size_t size, WebPIterator* frame, int* w, int* h) {
  WebPData data; WebPDemuxer* demux; WebPBitstreamFeatures features;
  if (!bytes || size < 20 || size > INPUT_LIMIT || memcmp(bytes,"RIFF",4) || memcmp(bytes+8,"WEBP",4)) return NULL;
  if ((uint64_t)bytes[4] + ((uint64_t)bytes[5]<<8) + ((uint64_t)bytes[6]<<16) + ((uint64_t)bytes[7]<<24) + 8 != size) return NULL;
  data.bytes = bytes; data.size = size; demux = WebPDemux(&data); if (!demux) return NULL;
  *w = (int)WebPDemuxGetI(demux,WEBP_FF_CANVAS_WIDTH); *h = (int)WebPDemuxGetI(demux,WEBP_FF_CANVAS_HEIGHT);
  memset(frame,0,sizeof(*frame));
  if (*w <= 0 || *h <= 0 || !WebPDemuxGetFrame(demux,1,frame)) { WebPDemuxDelete(demux); return NULL; }
  if (!frame->complete || frame->width <= 0 || frame->height <= 0 || frame->x_offset < 0 || frame->y_offset < 0 ||
      (int64_t)frame->x_offset+frame->width > *w || (int64_t)frame->y_offset+frame->height > *h ||
      (uint64_t)frame->width*frame->height*4 > BUDGET ||
      WebPGetFeatures(frame->fragment.bytes,frame->fragment.size,&features) != VP8_STATUS_OK || features.width != frame->width || features.height != frame->height) {
    WebPDemuxReleaseIterator(frame); WebPDemuxDelete(demux); return NULL;
  }
  return demux;
}
EXPORT int dh_webp_info(const uint8_t* input,size_t size,int* width,int* height) {
  WebPIterator frame; WebPDemuxer* demux; if (!width || !height) return 1;
  demux = open_webp(input,size,&frame,width,height); if (!demux) return 1;
  WebPDemuxReleaseIterator(&frame); WebPDemuxDelete(demux); return 0;
}
EXPORT int dh_webp_render(const uint8_t* input,size_t size,int width,int height,uint8_t* output,size_t output_size) {
  WebPIterator frame; WebPDemuxer* demux; WebPDecoderConfig config; int cw,ch,left,top,right,bottom,fw,fh,x,y,status=1; uint32_t bg; uint8_t* region=NULL; size_t canvas_bytes,region_bytes;
  if (width <= 0 || height <= 0 || width > 1600 || height > 1600 || !output) return 1;
  canvas_bytes=(size_t)width*height*4;
  if (canvas_bytes > BUDGET/3 || output_size != canvas_bytes) return 1;
  demux=open_webp(input,size,&frame,&cw,&ch); if (!demux) return 1;
  left=(int)((int64_t)frame.x_offset*width/cw); top=(int)((int64_t)frame.y_offset*height/ch);
  right=(int)(((int64_t)(frame.x_offset+frame.width)*width+cw-1)/cw); bottom=(int)(((int64_t)(frame.y_offset+frame.height)*height+ch-1)/ch);
  if(right>width)right=width; if(bottom>height)bottom=height; fw=right-left; fh=bottom-top;
  region_bytes=(size_t)fw*fh*4; if(fw<=0||fh<=0||region_bytes+canvas_bytes*2>BUDGET)goto done;
  region=(uint8_t*)malloc(region_bytes); if(!region)goto done;
  if(!WebPInitDecoderConfig(&config))goto done;
  config.options.use_scaling=1;config.options.scaled_width=fw;config.options.scaled_height=fh;config.options.use_threads=0;
  config.output.colorspace=MODE_BGRA;config.output.is_external_memory=1;config.output.u.RGBA.rgba=region;config.output.u.RGBA.stride=fw*4;config.output.u.RGBA.size=region_bytes;
  if(WebPDecode(frame.fragment.bytes,frame.fragment.size,&config)!=VP8_STATUS_OK){WebPFreeDecBuffer(&config.output);goto done;}
  WebPFreeDecBuffer(&config.output);
  bg=(WebPDemuxGetI(demux,WEBP_FF_FORMAT_FLAGS)&ANIMATION_FLAG)?WebPDemuxGetI(demux,WEBP_FF_BACKGROUND_COLOR):0;
  for(size_t i=0;i<canvas_bytes;i+=4){output[i]=(uint8_t)bg;output[i+1]=(uint8_t)(bg>>8);output[i+2]=(uint8_t)(bg>>16);output[i+3]=(uint8_t)(bg>>24);}
  for(y=0;y<fh;y++)for(x=0;x<fw;x++){
    uint8_t* s=region+((size_t)y*fw+x)*4;uint8_t* d=output+((size_t)(top+y)*width+left+x)*4;
    if(frame.blend_method==WEBP_MUX_NO_BLEND || s[3]==255){memcpy(d,s,4);}
    else {unsigned sa=s[3],da=d[3],oa=sa*255+da*(255-sa);for(int c=0;c<3;c++)d[c]=oa?(uint8_t)((s[c]*sa*255+d[c]*da*(255-sa)+oa/2)/oa):0;d[3]=(uint8_t)((oa+127)/255);}
  }
  status=0;
done: free(region);WebPDemuxReleaseIterator(&frame);WebPDemuxDelete(demux);return status;
}
