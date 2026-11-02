// Loaded into explorer.exe to give the taskbar's button highlights RoundedTB's corner radius.
//
// Getting in: RoundedTB hooks the taskbar thread with HookProc (which loads this DLL into Explorer)
// and sends the taskbar ApplyMessage: wParam is the radius x100 (negative restores Explorer's own
// corners), lParam 1 logs to %TEMP%\RoundedTB.Tap.log. The first message starts XAML diagnostics on
// Explorer's own process with this DLL as the TAP; later ones just restyle what it has found.
//
// Everything goes through IVisualTreeService rather than typed XAML classes, so the same code works
// whether the taskbar is built on system XAML or WinUI 3.

#include <windows.h>
#include <ocidl.h>
#include <xamlOM.h>
#include <winrt/base.h>
#include <algorithm>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

// {5F7C9D1E-6B3A-4C2E-9F1D-8A7B6C5D4E3F}
static const CLSID CLSID_Tap = { 0x5f7c9d1e, 0x6b3a, 0x4c2e, { 0x9f, 0x1d, 0x8a, 0x7b, 0x6c, 0x5d, 0x4e, 0x3f } };

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{
    struct Highlight
    {
        winrt::com_ptr<IVisualTreeService3> tree;
        InstanceHandle element;
        unsigned int cornerRadius;
        std::wstring xamlNamespace;
    };

    // Recursive: restyling can raise tree-change callbacks on the same thread.
    std::recursive_mutex lock;
    bool started = false;
    double radius = -1;
    std::wstring logPath;
    std::vector<Highlight> highlights;

    void Log(const std::wstring& line)
    {
        if (!logPath.empty())
        {
            std::wofstream(logPath, std::ios::app) << line << std::endl;
        }
    }

    void Style(const Highlight& highlight, double value)
    {
        if (value < 0)
        {
            highlight.tree->ClearProperty(highlight.element, highlight.cornerRadius);
            return;
        }
        InstanceHandle cornerRadius = 0;
        BSTR typeName = SysAllocString((highlight.xamlNamespace + L".CornerRadius").c_str());
        BSTR text = SysAllocString(std::to_wstring(value).c_str());
        if (SUCCEEDED(highlight.tree->CreateInstance(typeName, text, &cornerRadius)))
        {
            highlight.tree->SetProperty(highlight.element, cornerRadius, highlight.cornerRadius);
        }
        SysFreeString(typeName);
        SysFreeString(text);
    }

    bool FindProperty(IVisualTreeService3* tree, InstanceHandle element, const wchar_t* property, unsigned int& index)
    {
        unsigned int sourceCount = 0, propertyCount = 0;
        PropertyChainSource* sources = nullptr;
        PropertyChainValue* values = nullptr;
        if (FAILED(tree->GetPropertyValuesChain(element, &sourceCount, &sources, &propertyCount, &values)))
        {
            return false;
        }
        bool found = false;
        for (unsigned int i = 0; i < propertyCount; i++)
        {
            if (!found && values[i].PropertyName && wcscmp(values[i].PropertyName, property) == 0)
            {
                index = values[i].Index;
                found = true;
            }
            SysFreeString(values[i].Type);
            SysFreeString(values[i].DeclaringType);
            SysFreeString(values[i].ValueType);
            SysFreeString(values[i].ItemType);
            SysFreeString(values[i].Value);
            SysFreeString(values[i].PropertyName);
        }
        for (unsigned int i = 0; i < sourceCount; i++)
        {
            SysFreeString(sources[i].TargetType);
            SysFreeString(sources[i].Name);
        }
        CoTaskMemFree(values);
        CoTaskMemFree(sources);
        return found;
    }
}

struct Tap : winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
{
    winrt::com_ptr<IXamlDiagnostics> diagnostics;
    winrt::com_ptr<IVisualTreeService3> tree;

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override
    {
        diagnostics = nullptr;
        tree = nullptr;
        if (!site || FAILED(site->QueryInterface(IID_PPV_ARGS(diagnostics.put()))))
        {
            return site ? E_FAIL : S_OK;
        }
        tree = diagnostics.as<IVisualTreeService3>();

        // Advising on the calling thread deadlocks. The existing tree is replayed as Add events.
        winrt::com_ptr<IVisualTreeService3> service = tree;
        winrt::com_ptr<Tap> self;
        self.copy_from(this);
        std::thread([service, self]() { service->AdviseVisualTreeChange(self.get()); }).detach();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) noexcept override
    {
        return diagnostics ? diagnostics->QueryInterface(riid, site) : E_FAIL;
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element, VisualMutationType mutation) noexcept override
    {
        std::lock_guard guard(lock);
        if (mutation == Remove)
        {
            std::erase_if(highlights, [&](const Highlight& h) { return h.element == element.Handle; });
            return S_OK;
        }

        std::wstring type = element.Type ? element.Type : L"";
        std::wstring name = element.Name ? element.Name : L"";
        Log(std::to_wstring(relation.Parent) + L" -> " + std::to_wstring(element.Handle) + L" " + type + L" #" + name);

        // The hover/pressed highlight of every taskbar button, Start and the tray included.
        if (name != L"BackgroundElement" || !type.ends_with(L".Xaml.Controls.Border"))
        {
            return S_OK;
        }
        unsigned int index;
        if (!FindProperty(tree.get(), element.Handle, L"CornerRadius", index))
        {
            return S_OK;
        }
        Highlight highlight{ tree, element.Handle, index, type.substr(0, type.find(L".Controls.")) };
        highlights.push_back(highlight);
        Style(highlight, radius);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override
    {
        return S_OK;
    }
};

struct TapFactory : winrt::implements<TapFactory, IClassFactory>
{
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) noexcept override
    {
        *result = nullptr;
        if (outer)
        {
            return CLASS_E_NOAGGREGATION;
        }
        try
        {
            return winrt::make<Tap>().as<IUnknown>()->QueryInterface(riid, result);
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override
    {
        return S_OK;
    }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (clsid != CLSID_Tap)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    try
    {
        return winrt::make<TapFactory>().as<IUnknown>()->QueryInterface(riid, result);
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

// Explorer keeps the TAP for its lifetime; the module must never unload under it.
STDAPI DllCanUnloadNow()
{
    return S_FALSE;
}

static void Start()
{
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), self, MAX_PATH);

    // The taskbar hosts both system XAML and WinUI 3; attach to whichever are loaded. The endpoint
    // must be one of the framework's own connection names, and ERROR_NOT_FOUND means "try the next".
    using InitializeXamlDiagnosticsExFn = HRESULT(WINAPI*)(LPCWSTR, DWORD, LPCWSTR, LPCWSTR, CLSID, LPCWSTR);
    struct Framework { const wchar_t* module; const wchar_t* endpoint; };
    for (Framework framework : { Framework{ L"Microsoft.Internal.FrameworkUdk.dll", L"WinUIVisualDiagConnection" }, Framework{ L"Windows.UI.Xaml.dll", L"VisualDiagConnection" } })
    {
        HMODULE module = GetModuleHandleW(framework.module);
        auto initialize = module ? reinterpret_cast<InitializeXamlDiagnosticsExFn>(GetProcAddress(module, "InitializeXamlDiagnosticsEx")) : nullptr;
        if (!initialize)
        {
            continue;
        }
        HRESULT hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        for (int attempt = 1; attempt <= 1000 && hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND); attempt++)
        {
            hr = initialize((framework.endpoint + std::to_wstring(attempt)).c_str(), GetCurrentProcessId(), L"", self, CLSID_Tap, nullptr);
        }
        std::lock_guard guard(lock);
        Log(std::wstring(framework.module) + L": InitializeXamlDiagnosticsEx hr=" + std::to_wstring(hr));
    }
}

extern "C" __declspec(dllexport) LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam)
{
    static const UINT applyMessage = RegisterWindowMessageW(L"RoundedTB.Tap.Apply");
    auto* message = reinterpret_cast<CWPSTRUCT*>(lParam);
    if (code == HC_ACTION && message->message == applyMessage)
    {
        std::lock_guard guard(lock);
        radius = static_cast<double>(static_cast<INT_PTR>(message->wParam)) / 100.0;
        if (message->lParam == 1)
        {
            wchar_t temp[MAX_PATH];
            GetTempPathW(MAX_PATH, temp);
            logPath = std::wstring(temp) + L"RoundedTB.Tap.log";
        }
        if (!started)
        {
            started = true;
            std::thread(Start).detach();
        }
        else
        {
            for (const Highlight& highlight : highlights)
            {
                Style(highlight, radius);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}
