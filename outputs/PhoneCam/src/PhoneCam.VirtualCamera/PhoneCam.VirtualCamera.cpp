// PhoneCam virtual cameras: a DirectShow source filter per phone slot (loaded by OBS, Teams, Zoom,
// browsers) plus the sender API PhoneCam itself calls to push frames. Both halves live in this one
// DLL and meet in a named shared-memory frame buffer. Based on softcam (MIT, see softcam/LICENSE).

#include <olectl.h>
#include <initguid.h>

#include "Cameras.h"
#include "softcam/DShowSoftcam.h"
#include "softcam/SenderAPI.h"

// {1C15ECD3-BE4A-4643-BC8E-D570C72CD3C8}
DEFINE_GUID(CLSID_PhoneCamIphone,
0x1c15ecd3, 0xbe4a, 0x4643, 0xbc, 0x8e, 0xd5, 0x70, 0xc7, 0x2c, 0xd3, 0xc8);

namespace phonecam {

// Index = camera id used by the sender API (PhoneCam's VirtualCamera.cs). Android gets index 1
// with its own CLSID once that stage starts.
const CameraInfo g_cameras[] =
{
    { L"PhoneCam iPhone", &CLSID_PhoneCamIphone, "PhoneCam/iphone", 1280, 720, 60.0f },
};
const int g_camera_count = sizeof(g_cameras) / sizeof(g_cameras[0]);

} // namespace phonecam

namespace {

const AMOVIESETUP_MEDIATYPE s_pin_types[] =
{
    {
        &MEDIATYPE_Video,       // Major type
        &MEDIASUBTYPE_NULL      // Minor type
    }
};

const AMOVIESETUP_PIN s_pins[] =
{
    {
        const_cast<LPWSTR>(L"Output"),  // Pin string name
        FALSE,                  // Is it rendered
        TRUE,                   // Is it an output
        FALSE,                  // Can we have none
        FALSE,                  // Can we have many
        &CLSID_NULL,            // Connects to filter
        NULL,                   // Connects to pin
        1,                      // Number of types
        s_pin_types             // Pin details
    }
};

const REGFILTER2 s_reg_filter2 =
{
    1,
    MERIT_DO_NOT_USE,
    1,
    s_pins
};

CUnknown * WINAPI CreateIphoneCamera(LPUNKNOWN lpunk, HRESULT *phr)
{
    return softcam::Softcam::CreateInstance(lpunk, phonecam::g_cameras[0], phr);
}

// Registers (or removes) every camera in the video input device category.
HRESULT RegisterCameras(bool reg)
{
    HRESULT hr = AMovieDllRegisterServer2(reg ? TRUE : FALSE);
    if (FAILED(hr))
    {
        return hr;
    }
    hr = CoInitialize(nullptr);
    if (FAILED(hr))
    {
        return hr;
    }
    IFilterMapper2 *pFM2 = nullptr;
    hr = CoCreateInstance(
            CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
            IID_IFilterMapper2, (void**)&pFM2);
    if (SUCCEEDED(hr))
    {
        for (int i = 0; i < phonecam::g_camera_count && SUCCEEDED(hr); i++)
        {
            const phonecam::CameraInfo& camera = phonecam::g_cameras[i];
            pFM2->UnregisterFilter(&CLSID_VideoInputDeviceCategory, 0, *camera.clsid);
            if (reg)
            {
                hr = pFM2->RegisterFilter(
                        *camera.clsid,
                        camera.name,
                        0,
                        &CLSID_VideoInputDeviceCategory,
                        camera.name,
                        &s_reg_filter2);
            }
        }
        pFM2->Release();
    }
    CoFreeUnusedLibraries();
    CoUninitialize();
    return hr;
}

} // namespace

// COM global table of objects in this dll (one entry per phonecam::g_cameras entry)

CFactoryTemplate g_Templates[] =
{
    {
        phonecam::g_cameras[0].name,
        &CLSID_PhoneCamIphone,
        &CreateIphoneCamera,
        NULL,
        nullptr
    }
};
int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);

STDAPI DllRegisterServer()
{
    return RegisterCameras(true);
}

STDAPI DllUnregisterServer()
{
    return RegisterCameras(false);
}

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);

BOOL APIENTRY DllMain(HANDLE hModule, DWORD  dwReason, LPVOID lpReserved)
{
    return DllEntryPoint((HINSTANCE)(hModule), dwReason, lpReserved);
}


//
// Sender API for PhoneCam (P/Invoke). Frames are 24-bit BGR, top-down, width*height*3 bytes.
//

extern "C" void* __cdecl pcCreateCamera(int camera, int width, int height)
{
    // Framerate 0: frames go out as they arrive; the phone sets the pace.
    return softcam::sender::CreateCamera(camera, width, height, 0.0f);
}

extern "C" void __cdecl pcDeleteCamera(void* camera)
{
    softcam::sender::DeleteCamera(camera);
}

extern "C" void __cdecl pcSendFrame(void* camera, const void* image_bits)
{
    softcam::sender::SendFrame(camera, image_bits);
}

extern "C" bool __cdecl pcIsConnected(void* camera)
{
    return softcam::sender::IsConnected(camera);
}
