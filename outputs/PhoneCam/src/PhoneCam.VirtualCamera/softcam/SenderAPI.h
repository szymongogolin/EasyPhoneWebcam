#pragma once


namespace softcam {
namespace sender {

using CameraHandle = void*;

// PhoneCam: `camera` indexes phonecam::g_cameras; each camera has its own frame buffer and
// at most one sender at a time.
CameraHandle    CreateCamera(int camera, int width, int height, float framerate = 60.0f);
void            DeleteCamera(CameraHandle camera);
void            SendFrame(CameraHandle camera, const void* image_bits);
bool            WaitForConnection(CameraHandle camera, float timeout = 0.0f);
bool            IsConnected(CameraHandle camera);

} //namespace sender
} //namespace softcam
