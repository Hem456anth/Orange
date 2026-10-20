// Orange Windows 11 top-level context menu: an IExplorerCommand with a submenu, registered through a sparse MSIX
// package (see AppxManifest.xml). Each command starts Orange.exe with the selected files; the DLL itself converts
// nothing, opens no network, and reads only %LOCALAPPDATA%\Orange\menu.txt (pinned preset names written by the app).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/module.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include <fstream>
#include <sstream>
#include <algorithm>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "runtimeobject.lib")

using namespace Microsoft::WRL;

static HMODULE g_module = nullptr;

static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    return path;
}

static std::wstring Quote(const std::wstring& s)
{
    std::wstring r = L"\"";
    for (wchar_t c : s) { if (c == L'"') r += L'\\'; r += c; }
    return r + L"\"";
}

static std::vector<std::wstring> Selection(IShellItemArray* items)
{
    std::vector<std::wstring> out;
    DWORD n = 0;
    if (!items || FAILED(items->GetCount(&n))) return out;
    for (DWORD i = 0; i < n && i < 500; i++)
    {
        ComPtr<IShellItem> it;
        if (FAILED(items->GetItemAt(i, &it))) continue;
        PWSTR p = nullptr;
        if (SUCCEEDED(it->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p) { out.emplace_back(p); CoTaskMemFree(p); }
    }
    return out;
}

static std::wstring Lower(std::wstring s) { std::transform(s.begin(), s.end(), s.begin(), ::towlower); return s; }

static std::wstring KindOf(const std::wstring& path)
{
    auto dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos) return L"";
    auto e = Lower(path.substr(dot + 1));
    static const wchar_t* img[] = { L"png", L"jpg", L"jpeg", L"webp", L"heic", L"tif", L"tiff", L"bmp", L"gif", L"avif", L"jxl", L"dng", L"cr2", L"nef", L"arw" };
    static const wchar_t* vid[] = { L"mp4", L"mov", L"mkv", L"webm", L"avi", L"m4v", L"wmv" };
    static const wchar_t* aud[] = { L"mp3", L"wav", L"flac", L"m4a", L"aac", L"ogg", L"opus", L"aiff", L"wma" };
    for (auto x : img) if (e == x) return L"image";
    for (auto x : vid) if (e == x) return L"video";
    for (auto x : aud) if (e == x) return L"audio";
    if (e == L"pdf") return L"pdf";
    if (e == L"docx" || e == L"md" || e == L"txt" || e == L"html") return L"document";
    if (e == L"xlsx" || e == L"xls" || e == L"ods" || e == L"csv") return L"spreadsheet";
    if (e == L"pptx") return L"presentation";
    return L"";
}

static std::wstring CommonKind(IShellItemArray* items)
{
    auto files = Selection(items);
    std::wstring kind;
    for (auto& f : files) { auto k = KindOf(f); if (k.empty() || (!kind.empty() && k != kind)) return L""; kind = k; }
    return kind;
}

static void Launch(const std::wstring& verbArgs, IShellItemArray* items)
{
    auto files = Selection(items);
    if (files.empty()) return;
    std::wstring cmd = Quote(ModuleDir() + L"\\Orange.exe") + L" " + verbArgs;
    for (auto& f : files) cmd += L" " + Quote(f);
    STARTUPINFOW si{ sizeof(si) }; PROCESS_INFORMATION pi{};
    std::vector<wchar_t> buf(cmd.begin(), cmd.end()); buf.push_back(0);
    if (CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
}

// ---------------------------------------------------------------- leaf command

class LeafCommand : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    LeafCommand(std::wstring title, std::wstring args, std::wstring kind) : _title(std::move(title)), _args(std::move(args)), _kind(std::move(kind)) {}
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(_title.c_str(), name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override { *icon = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* g) override { *g = GUID_NULL; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        *state = (_kind.empty() || CommonKind(items) == _kind) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override { Launch(_args, items); return S_OK; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* f) override { *f = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }
private:
    std::wstring _title, _args, _kind;
};

class CommandEnum : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
{
public:
    explicit CommandEnum(std::vector<ComPtr<IExplorerCommand>> cmds) : _cmds(std::move(cmds)) {}
    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** out, ULONG* fetched) override
    {
        ULONG n = 0;
        for (; n < celt && _pos < _cmds.size(); n++, _pos++) _cmds[_pos].CopyTo(&out[n]);
        if (fetched) *fetched = n;
        return n == celt ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG celt) override { _pos = (std::min)(_pos + celt, (ULONG)_cmds.size()); return S_OK; }
    IFACEMETHODIMP Reset() override { _pos = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }
private:
    std::vector<ComPtr<IExplorerCommand>> _cmds;
    ULONG _pos = 0;
};

// ---------------------------------------------------------------- root "Orange" submenu

class __declspec(uuid("7C5B9C3E-9B1E-4E43-9A2F-0A7E0F0A0002")) OrangeRootCommand
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(L"Orange", name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override { return SHStrDupW((ModuleDir() + L"\\Orange.exe,0").c_str(), icon); }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* g) override { *g = __uuidof(OrangeRootCommand); return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override { *state = ECS_ENABLED; return S_OK; }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override { return S_OK; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* f) override { *f = ECF_HASSUBCOMMANDS; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override
    {
        std::vector<ComPtr<IExplorerCommand>> cmds;
        cmds.push_back(Make<LeafCommand>(L"Open wheel", L"--wheel", L""));
        // Pinned presets written by the app: "preset|Name|kind".
        wchar_t local[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH))
        {
            std::wifstream f(std::wstring(local) + L"\\Orange\\menu.txt");
            std::wstring line; int count = 0;
            while (count < 6 && std::getline(f, line))
            {
                std::wstringstream ss(line); std::wstring type, name, kind;
                std::getline(ss, type, L'|'); std::getline(ss, name, L'|'); std::getline(ss, kind, L'|');
                if (type != L"preset" || name.empty() || name.size() > 60) continue;
                cmds.push_back(Make<LeafCommand>(name, L"--preset " + Quote(name), kind)); count++;
            }
        }
        // Top formats per kind (hidden when the selection is a different kind).
        struct F { const wchar_t* kind; const wchar_t* title; const wchar_t* fmt; };
        static const F formats[] = {
            { L"image", L"Convert to WebP", L"webp" }, { L"image", L"Convert to JPG", L"jpg" }, { L"image", L"Convert to PNG", L"png" }, { L"image", L"Convert to PDF", L"pdf" },
            { L"video", L"Convert to MP4", L"mp4" }, { L"video", L"Convert to MP3", L"mp3" }, { L"video", L"Convert to GIF", L"gif" },
            { L"audio", L"Convert to MP3", L"mp3" }, { L"audio", L"Convert to WAV", L"wav" },
            { L"pdf", L"Convert to DOCX", L"docx" }, { L"pdf", L"Convert to TXT", L"txt" },
            { L"document", L"Convert to PDF", L"pdf" }, { L"document", L"Convert to DOCX", L"docx" },
            { L"spreadsheet", L"Convert to XLSX", L"xlsx" }, { L"spreadsheet", L"Convert to PDF", L"pdf" },
            { L"presentation", L"Convert to PDF", L"pdf" },
        };
        for (auto& x : formats) cmds.push_back(Make<LeafCommand>(x.title, std::wstring(L"--to ") + x.fmt, x.kind));
        *e = Make<CommandEnum>(std::move(cmds)).Detach();
        return S_OK;
    }
    IFACEMETHODIMP SetSite(IUnknown* site) override { _site = site; return S_OK; }
    IFACEMETHODIMP GetSite(REFIID riid, void** out) override { return _site ? _site.CopyTo(riid, out) : E_FAIL; }
private:
    ComPtr<IUnknown> _site;
};

CoCreatableClass(OrangeRootCommand)

STDAPI DllGetActivationFactory(HSTRING id, IActivationFactory** f) { return Module<ModuleType::InProc>::GetModule().GetActivationFactory(id, f); }
STDAPI DllCanUnloadNow() { return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE; }
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) { return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv); }

BOOL APIENTRY DllMain(HMODULE m, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = m; DisableThreadLibraryCalls(m); }
    return TRUE;
}
