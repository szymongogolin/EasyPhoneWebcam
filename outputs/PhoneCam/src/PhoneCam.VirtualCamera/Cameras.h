#pragma once

#include <windows.h>

namespace phonecam {

// One DirectShow video capture device per phone slot. The format is fixed so the device opens even
// while PhoneCam is not running; PhoneCam always sends frames of exactly this size.
struct CameraInfo
{
    const wchar_t*  name;               // shown in OBS, Teams, Zoom, browsers
    const GUID*     clsid;
    const char*     frame_buffer_name;  // prefix of the shared memory and mutex names
    int             width;
    int             height;
    float           framerate;          // advertised; frames are forwarded as they arrive
};

constexpr int kMaxCameras = 2;

extern const CameraInfo g_cameras[];
extern const int        g_camera_count;

} // namespace phonecam
