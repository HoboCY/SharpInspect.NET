// Compile only against the separately supplied vendor Windows SDK headers.
// This file does not link, load a DLL, enumerate a camera or grant production compatibility.
#include <stddef.h>
#include "MvCameraControl.h"
static_assert(sizeof(void*) == 8, "x64 required");
static_assert(sizeof(MV_CC_DEVICE_INFO) == 572, "device information size");
static_assert(offsetof(MV_CC_DEVICE_INFO, nTLayerType) == 12, "transport type offset");
static_assert(offsetof(MV_CC_DEVICE_INFO, SpecialInfo) == 32, "device union offset");
static_assert(offsetof(MV_GIGE_DEVICE_INFO, chModelName) == 52, "GigE model offset");
static_assert(offsetof(MV_GIGE_DEVICE_INFO, chDeviceVersion) == 84, "GigE firmware offset");
static_assert(offsetof(MV_GIGE_DEVICE_INFO, chSerialNumber) == 164, "GigE serial offset");
static_assert(offsetof(MV_USB3_DEVICE_INFO, chModelName) == 140, "USB3 model offset");
static_assert(offsetof(MV_USB3_DEVICE_INFO, chDeviceVersion) == 268, "USB3 firmware offset");
static_assert(offsetof(MV_USB3_DEVICE_INFO, chSerialNumber) == 396, "USB3 serial offset");
static_assert(sizeof(MV_CC_DEVICE_INFO_LIST) == 2056, "device list size");
static_assert(offsetof(MV_CC_DEVICE_INFO_LIST, nDeviceNum) == 0, "device count offset");
static_assert(offsetof(MV_CC_DEVICE_INFO_LIST, pDeviceInfo) == 8, "device pointers offset");
static_assert(sizeof(MV_FRAME_OUT_INFO_EX) == 256, "frame info size");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nWidth) == 0, "width offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nHeight) == 2, "height offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, enPixelType) == 4, "pixel format offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nFrameNum) == 8, "frame counter offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nDevTimeStampHigh) == 12, "timestamp high offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nDevTimeStampLow) == 16, "timestamp low offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nFrameLen) == 32, "frame length offset");
static_assert(offsetof(MV_FRAME_OUT_INFO_EX, nLostPacket) == 96, "lost packets offset");
static_assert(sizeof(MVCC_INTVALUE_EX) == 96, "integer node size");
static_assert(offsetof(MVCC_INTVALUE_EX, nCurValue) == 0, "integer current offset");
static_assert(offsetof(MVCC_INTVALUE_EX, nMax) == 8, "integer maximum offset");
static_assert(offsetof(MVCC_INTVALUE_EX, nMin) == 16, "integer minimum offset");
static_assert(offsetof(MVCC_INTVALUE_EX, nInc) == 24, "integer increment offset");
static_assert(sizeof(MVCC_FLOATVALUE) == 28, "float node size");
static_assert(offsetof(MVCC_FLOATVALUE, fCurValue) == 0, "float current offset");
static_assert(offsetof(MVCC_FLOATVALUE, fMax) == 4, "float maximum offset");
static_assert(offsetof(MVCC_FLOATVALUE, fMin) == 8, "float minimum offset");
static_assert(sizeof(MVCC_ENUMVALUE) == 280, "enum node size");
static_assert(offsetof(MVCC_ENUMVALUE, nCurValue) == 0, "enum current offset");
static_assert(offsetof(MVCC_ENUMVALUE, nSupportedNum) == 4, "enum count offset");
static_assert(offsetof(MVCC_ENUMVALUE, nSupportValue) == 8, "enum values offset");
static_assert(sizeof(MVCC_STRINGVALUE) == 272, "string node size");
static_assert(offsetof(MVCC_STRINGVALUE, chCurValue) == 0, "string current offset");
static_assert(sizeof(MVCC_ENUMENTRY) == 84, "enum symbolic entry size");
static_assert(offsetof(MVCC_ENUMENTRY, nValue) == 0, "enum entry value offset");
static_assert(offsetof(MVCC_ENUMENTRY, chSymbolic) == 4, "enum entry symbol offset");
