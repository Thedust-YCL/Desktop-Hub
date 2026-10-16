#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <memory>
#include <mutex>
#include <objidl.h>
#include <shcore.h>
#include <windows.h>
#include <winrt/windows.data.pdf.h>
#include <winrt/windows.foundation.h>
#include <winrt/windows.storage.streams.h>
#include <wrl.h>
using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
struct FileState
{
    HANDLE handle;
    std::mutex gate;
    explicit FileState(HANDLE input)
    {
        if (!DuplicateHandle(GetCurrentProcess(), input, GetCurrentProcess(), &handle, 0, FALSE, DUPLICATE_SAME_ACCESS))
            winrt::throw_last_error();
    }
    ~FileState()
    {
        CloseHandle(handle);
    }
};
class FileStream final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IStream>
{
    std::shared_ptr<FileState> file;
    ULONGLONG position = 0;

  public:
    explicit FileStream(std::shared_ptr<FileState> state, ULONGLONG pos = 0) : file(std::move(state)), position(pos)
    {
    }
    HRESULT STDMETHODCALLTYPE Read(void *data, ULONG count, ULONG *read) override
    {
        std::lock_guard<std::mutex> lock(file->gate);
        if (read)
            *read = 0;
        LARGE_INTEGER offset;
        offset.QuadPart = position;
        if (!SetFilePointerEx(file->handle, offset, nullptr, FILE_BEGIN))
            return HRESULT_FROM_WIN32(GetLastError());
        DWORD n = 0;
        if (!ReadFile(file->handle, data, count, &n, nullptr))
            return HRESULT_FROM_WIN32(GetLastError());
        position += n;
        if (read)
            *read = n;
        return n == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Write(void const *, ULONG, ULONG *) override
    {
        return STG_E_ACCESSDENIED;
    }
    HRESULT STDMETHODCALLTYPE Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER *result) override
    {
        std::lock_guard<std::mutex> lock(file->gate);
        LARGE_INTEGER size;
        if (!GetFileSizeEx(file->handle, &size))
            return HRESULT_FROM_WIN32(GetLastError());
        LONGLONG start = origin == STREAM_SEEK_SET   ? 0
                         : origin == STREAM_SEEK_CUR ? (LONGLONG)position
                         : origin == STREAM_SEEK_END ? size.QuadPart
                                                     : -1;
        if (start < 0 || move.QuadPart < -start || move.QuadPart > LLONG_MAX - start)
            return STG_E_INVALIDFUNCTION;
        position = start + move.QuadPart;
        if (result)
            result->QuadPart = position;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSize(ULARGE_INTEGER) override
    {
        return STG_E_ACCESSDENIED;
    }
    HRESULT STDMETHODCALLTYPE CopyTo(IStream *, ULARGE_INTEGER, ULARGE_INTEGER *, ULARGE_INTEGER *) override
    {
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE Commit(DWORD) override
    {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Revert() override
    {
        return STG_E_ACCESSDENIED;
    }
    HRESULT STDMETHODCALLTYPE LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override
    {
        return STG_E_INVALIDFUNCTION;
    }
    HRESULT STDMETHODCALLTYPE UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override
    {
        return STG_E_INVALIDFUNCTION;
    }
    HRESULT STDMETHODCALLTYPE Stat(STATSTG *stat, DWORD) override
    {
        if (!stat)
            return E_POINTER;
        std::lock_guard<std::mutex> lock(file->gate);
        LARGE_INTEGER size;
        if (!GetFileSizeEx(file->handle, &size))
            return HRESULT_FROM_WIN32(GetLastError());
        *stat = {};
        stat->type = STGTY_STREAM;
        stat->cbSize.QuadPart = size.QuadPart;
        stat->grfMode = STGM_READ;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Clone(IStream **result) override
    {
        if (!result)
            return E_POINTER;
        std::lock_guard<std::mutex> lock(file->gate);
        return Microsoft::WRL::Make<FileStream>(file, position).CopyTo(result);
    }
};
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <d3d11.h>
#include <vector>
#include <wincodec.h>
#include <windows.data.pdf.interop.h>
// DesktopHub-owned deterministic Windows PDF boundary. No WinRT/COM pointer escapes this call.
using CancelCheck = int(__cdecl *)();
static constexpr uint64_t Budget = 16ull * 1024 * 1024;
struct Apartment
{
    HRESULT initialized;
    Apartment() : initialized(RoInitialize(RO_INIT_MULTITHREADED))
    {
        winrt::check_hresult(initialized);
    }
    ~Apartment()
    {
        if (SUCCEEDED(initialized))
            RoUninitialize();
    }
};
static void CheckCancel(CancelCheck cancelled)
{
    if (cancelled && cancelled())
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_CANCELLED));
}
template <typename Operation> static auto Finish(Operation const &operation, CancelCheck cancelled)
{
    bool requested = false;
    // A cancelled call stays in this native scope until the underlying operation is terminal.
    while (operation.Status() == winrt::Windows::Foundation::AsyncStatus::Started)
    {
        if (!requested && cancelled && cancelled())
        {
            requested = true;
            operation.Cancel();
        }
        Sleep(10);
    }
    return operation.GetResults();
}
static std::vector<uint8_t> SoftwareRender(winrt::Windows::Data::Pdf::PdfPage const &page, int edge, int &width,
                                           int &height, CancelCheck cancelled)
{
    auto size = page.Size();
    double scale = std::min(1.0, (double)std::min(edge, 1100) / std::max(size.Width, size.Height));
    width = std::max(1, (int)std::floor(size.Width * scale));
    height = std::max(1, (int)std::floor(size.Height * scale));
    if ((uint64_t)width * height * 12 > Budget)
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    winrt::check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                           nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
    ComPtr<IDXGIDevice> dxgi;
    winrt::check_hresult(device.As(&dxgi));
    ComPtr<IPdfRendererNative> renderer;
    winrt::check_hresult(PdfCreateRenderer(dxgi.Get(), &renderer));
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = (UINT)width;
    desc.Height = (UINT)height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> target;
    winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, &target));
    ComPtr<IDXGISurface> surface;
    winrt::check_hresult(target.As(&surface));
    auto params = PdfRenderParams({}, (UINT)width, (UINT)height);
    CheckCancel(cancelled);
    winrt::check_hresult(renderer->RenderPageToSurface(page.as<::IUnknown>().get(), surface.Get(), POINT{}, &params));
    CheckCancel(cancelled);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, &staging));
    context->CopyResource(staging.Get(), target.Get());
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    winrt::check_hresult(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    struct Unmap
    {
        ID3D11DeviceContext *context;
        ID3D11Resource *resource;
        ~Unmap()
        {
            context->Unmap(resource, 0);
        }
    } unmap{context.Get(), staging.Get()};
    if ((uint64_t)mapped.RowPitch * height * 3 > Budget)
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
    ComPtr<IWICImagingFactory> wic;
    winrt::check_hresult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)));
    ComPtr<IStream> encoded;
    winrt::check_hresult(CreateStreamOnHGlobal(nullptr, TRUE, &encoded));
    ComPtr<IWICBitmapEncoder> encoder;
    winrt::check_hresult(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder));
    winrt::check_hresult(encoder->Initialize(encoded.Get(), WICBitmapEncoderNoCache));
    ComPtr<IWICBitmapFrameEncode> frame;
    winrt::check_hresult(encoder->CreateNewFrame(&frame, nullptr));
    winrt::check_hresult(frame->Initialize(nullptr));
    winrt::check_hresult(frame->SetSize((UINT)width, (UINT)height));
    auto format = GUID_WICPixelFormat32bppBGRA;
    winrt::check_hresult(frame->SetPixelFormat(&format));
    if (format != GUID_WICPixelFormat32bppBGRA)
        winrt::throw_hresult(E_UNEXPECTED);
    winrt::check_hresult(
        frame->WritePixels((UINT)height, mapped.RowPitch, mapped.RowPitch * (UINT)height, (BYTE *)mapped.pData));
    winrt::check_hresult(frame->Commit());
    winrt::check_hresult(encoder->Commit());
    CheckCancel(cancelled);
    STATSTG stat = {};
    winrt::check_hresult(encoded->Stat(&stat, STATFLAG_NONAME));
    if (stat.cbSize.QuadPart < 24 || stat.cbSize.QuadPart > Budget / 2)
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
    std::vector<uint8_t> png((size_t)stat.cbSize.QuadPart);
    LARGE_INTEGER start = {};
    winrt::check_hresult(encoded->Seek(start, STREAM_SEEK_SET, nullptr));
    ULONG read = 0;
    winrt::check_hresult(encoded->Read(png.data(), (ULONG)png.size(), &read));
    if (read != png.size())
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    return png;
}
static HRESULT RenderPdf(HANDLE input, int edge, CancelCheck cancelled, uint8_t **bytes, size_t *count, int *width,
                         int *height, uint32_t *pages) noexcept
{
    if (!bytes || !count || !width || !height || !pages)
        return E_POINTER;
    *bytes = nullptr;
    *count = 0;
    *width = 0;
    *height = 0;
    *pages = 0;
    if (!input || input == INVALID_HANDLE_VALUE || edge < 1 || edge > 1200)
        return E_INVALIDARG;
    try
    {
        Apartment apartment;
        CheckCancel(cancelled);
        auto stream = Microsoft::WRL::Make<FileStream>(std::make_shared<FileState>(input));
        winrt::Windows::Storage::Streams::IRandomAccessStream source{nullptr};
        winrt::check_hresult(CreateRandomAccessStreamOverStream(
            stream.Get(), BSOS_DEFAULT, winrt::guid_of<decltype(source)>(), winrt::put_abi(source)));
        std::vector<uint8_t> png;
        int finalWidth = 0, finalHeight = 0;
        uint32_t pageCount = 0;
        {
            auto document = Finish(winrt::Windows::Data::Pdf::PdfDocument::LoadFromStreamAsync(source), cancelled);
            CheckCancel(cancelled);
            pageCount = document.PageCount();
            if (!pageCount)
                winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
            auto page = document.GetPage(0);
            auto size = page.Size();
            if (!std::isfinite(size.Width) || !std::isfinite(size.Height) || size.Width <= 0 || size.Height <= 0 ||
                size.Width > 1e7 || size.Height > 1e7)
                winrt::throw_hresult(E_INVALIDARG);
            png = SoftwareRender(page, edge, finalWidth, finalHeight, cancelled);
            page.Close();
        }
        source.Close();
        source = nullptr;
        stream.Reset();
        CheckCancel(cancelled);
        if (png.empty())
            winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
        auto result = (uint8_t *)CoTaskMemAlloc(png.size());
        if (!result)
            winrt::throw_hresult(E_OUTOFMEMORY);
        memcpy(result, png.data(), png.size());
        *bytes = result;
        *count = png.size();
        *width = finalWidth;
        *height = finalHeight;
        *pages = pageCount;
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}
extern "C" __declspec(dllexport) HRESULT __cdecl dh_pdf_render(HANDLE input, int edge, CancelCheck cancelled,
                                                               uint8_t **bytes, size_t *count, int *width, int *height,
                                                               uint32_t *pages) noexcept
{
    return RenderPdf(input, edge, cancelled, bytes, count, width, height, pages);
}
