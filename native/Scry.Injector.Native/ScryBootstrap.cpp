#include "ScryBootstrap.h"

#include <metahost.h>
#include <stddef.h>
#include <strsafe.h>

#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:ScryBootstrapStart=_ScryBootstrapStart@4")
#pragma comment(linker, "/EXPORT:ScryBootstrapGetStatus=_ScryBootstrapGetStatus@4")
#else
#pragma comment(linker, "/EXPORT:ScryBootstrapStart")
#pragma comment(linker, "/EXPORT:ScryBootstrapGetStatus")
#endif

namespace
{
constexpr uint32_t kMaxWideStringLength = 32767;
constexpr uint32_t kMaxArgumentLength = 256 * 1024;
static_assert(sizeof(ScryBootstrapConfigV1) == 116, "Unexpected config ABI");
static_assert(
    sizeof(ScryBootstrapStatusRecordV1) == 560,
    "Unexpected status ABI");

struct ConfigPrefix
{
    uint32_t magic;
    uint16_t version;
    uint16_t header_size;
    uint32_t total_size;
};

struct ConfigView
{
    const ScryBootstrapConfigV1* config;
    const WCHAR* assembly_path;
    const WCHAR* runtime_config_path;
    const WCHAR* type_name;
    const WCHAR* method_name;
    const BYTE* argument;
    uint32_t argument_length;
    const WCHAR* hostfxr_path;
    const WCHAR* status_path;
    const WCHAR* completion_event_name;
};

SRWLOCK g_status_lock = SRWLOCK_INIT;
volatile LONG g_started = 0;
ScryBootstrapStatusRecordV1 g_status = {
    sizeof(ScryBootstrapStatusRecordV1),
    SCRY_BOOTSTRAP_VERSION,
    ScryBootstrapStageNotStarted,
    ScryBootstrapSuccess,
    0,
    0,
    0,
    0,
    0,
    0,
    {0, 0},
    L"Not started."
};

void SetStatus(
    uint32_t runtime_family,
    ScryBootstrapStage stage,
    ScryBootstrapStatusCode status_code,
    int32_t runtime_result,
    DWORD win32_error,
    int32_t managed_result,
    const WCHAR* message)
{
    ScryBootstrapStatusRecordV1 status = {};
    status.size = sizeof(status);
    status.version = SCRY_BOOTSTRAP_VERSION;
    status.stage = stage;
    status.status_code = status_code;
    status.runtime_result = runtime_result;
    status.win32_error = win32_error;
    status.managed_result = managed_result;
    status.runtime_family = runtime_family;
    status.process_id = GetCurrentProcessId();
    status.thread_id = GetCurrentThreadId();
    GetSystemTimeAsFileTime(&status.updated_at);
    if (message != nullptr)
    {
        (void)StringCchCopyW(status.message, ARRAYSIZE(status.message), message);
    }

    AcquireSRWLockExclusive(&g_status_lock);
    g_status = status;
    ReleaseSRWLockExclusive(&g_status_lock);
}

ScryBootstrapStatusRecordV1 GetStatusSnapshot()
{
    ScryBootstrapStatusRecordV1 status = {};
    AcquireSRWLockShared(&g_status_lock);
    status = g_status;
    ReleaseSRWLockShared(&g_status_lock);
    return status;
}

bool IsAbsolutePath(const WCHAR* value)
{
    if (value == nullptr || value[0] == L'\0')
    {
        return false;
    }

    if (value[0] == L'\\' && value[1] == L'\\')
    {
        return true;
    }

    return value[0] != L'\0' &&
        value[1] == L':' &&
        (value[2] == L'\\' || value[2] == L'/');
}

bool ReadWideString(
    const BYTE* base,
    uint32_t total_size,
    uint16_t header_size,
    ScryBootstrapBufferRef reference,
    bool required,
    const WCHAR** value)
{
    *value = nullptr;
    if (reference.length == 0)
    {
        return !required && reference.offset == 0;
    }

    if (reference.offset < header_size ||
        (reference.offset % sizeof(WCHAR)) != 0 ||
        reference.length > kMaxWideStringLength)
    {
        return false;
    }

    const uint64_t byte_count =
        (static_cast<uint64_t>(reference.length) + 1) * sizeof(WCHAR);
    const uint64_t end = static_cast<uint64_t>(reference.offset) + byte_count;
    if (end > total_size)
    {
        return false;
    }

    const WCHAR* text = reinterpret_cast<const WCHAR*>(base + reference.offset);
    if (text[reference.length] != L'\0')
    {
        return false;
    }

    for (uint32_t index = 0; index < reference.length; ++index)
    {
        if (text[index] == L'\0')
        {
            return false;
        }
    }

    *value = text;
    return true;
}

bool ReadByteBuffer(
    const BYTE* base,
    uint32_t total_size,
    uint16_t header_size,
    ScryBootstrapBufferRef reference,
    const BYTE** value)
{
    *value = nullptr;
    if (reference.length == 0)
    {
        return reference.offset == 0;
    }

    if (reference.offset < header_size ||
        reference.length > kMaxArgumentLength)
    {
        return false;
    }

    const uint64_t end =
        static_cast<uint64_t>(reference.offset) + reference.length;
    if (end > total_size)
    {
        return false;
    }

    *value = base + reference.offset;
    return true;
}

ScryBootstrapStatusCode ValidateConfig(const BYTE* bytes, ConfigView* view)
{
    const ScryBootstrapConfigV1* config =
        reinterpret_cast<const ScryBootstrapConfigV1*>(bytes);
    if (config->flags != 0)
    {
        return ScryBootstrapInvalidConfiguration;
    }

    for (size_t index = 0; index < ARRAYSIZE(config->reserved); ++index)
    {
        if (config->reserved[index] != 0)
        {
            return ScryBootstrapInvalidConfiguration;
        }
    }

    const bool modern =
        config->runtime_family == ScryBootstrapRuntimeModernDotNet;
    if (!modern &&
        config->runtime_family != ScryBootstrapRuntimeNetFramework)
    {
        return ScryBootstrapInvalidConfiguration;
    }

    view->config = config;
    if (!ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->assembly_path,
            true,
            &view->assembly_path) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->runtime_config_path,
            modern,
            &view->runtime_config_path) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->type_name,
            true,
            &view->type_name) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->method_name,
            true,
            &view->method_name) ||
        !ReadByteBuffer(
            bytes,
            config->total_size,
            config->header_size,
            config->argument,
            &view->argument) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->hostfxr_path,
            false,
            &view->hostfxr_path) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->status_path,
            false,
            &view->status_path) ||
        !ReadWideString(
            bytes,
            config->total_size,
            config->header_size,
            config->completion_event_name,
            false,
            &view->completion_event_name))
    {
        return ScryBootstrapInvalidConfiguration;
    }

    view->argument_length = config->argument.length;
    if (!IsAbsolutePath(view->assembly_path) ||
        (modern && !IsAbsolutePath(view->runtime_config_path)) ||
        (view->hostfxr_path != nullptr &&
            !IsAbsolutePath(view->hostfxr_path)) ||
        (view->status_path != nullptr &&
            !IsAbsolutePath(view->status_path)))
    {
        return ScryBootstrapInvalidConfiguration;
    }

    return ScryBootstrapSuccess;
}

BYTE* CopyAndValidateConfig(
    const void* configuration,
    ConfigView* view,
    ScryBootstrapStatusCode* status_code)
{
    *status_code = ScryBootstrapInvalidArgument;
    if (configuration == nullptr)
    {
        return nullptr;
    }

    ConfigPrefix prefix = {};
    __try
    {
        CopyMemory(&prefix, configuration, sizeof(prefix));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }

    if (prefix.magic != SCRY_BOOTSTRAP_MAGIC)
    {
        *status_code = ScryBootstrapInvalidConfiguration;
        return nullptr;
    }

    if (prefix.version != SCRY_BOOTSTRAP_VERSION)
    {
        *status_code = ScryBootstrapUnsupportedConfigurationVersion;
        return nullptr;
    }

    if (prefix.header_size < sizeof(ScryBootstrapConfigV1) ||
        prefix.total_size < prefix.header_size ||
        prefix.total_size > SCRY_BOOTSTRAP_MAX_CONFIG_SIZE)
    {
        *status_code = ScryBootstrapInvalidConfiguration;
        return nullptr;
    }

    BYTE* copy = static_cast<BYTE*>(
        HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, prefix.total_size));
    if (copy == nullptr)
    {
        *status_code = ScryBootstrapOutOfMemory;
        return nullptr;
    }

    __try
    {
        CopyMemory(copy, configuration, prefix.total_size);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        HeapFree(GetProcessHeap(), 0, copy);
        *status_code = ScryBootstrapInvalidArgument;
        return nullptr;
    }

    const ConfigPrefix* copied_prefix =
        reinterpret_cast<const ConfigPrefix*>(copy);
    if (copied_prefix->magic != prefix.magic ||
        copied_prefix->version != prefix.version ||
        copied_prefix->header_size != prefix.header_size ||
        copied_prefix->total_size != prefix.total_size)
    {
        HeapFree(GetProcessHeap(), 0, copy);
        *status_code = ScryBootstrapInvalidConfiguration;
        return nullptr;
    }

    *status_code = ValidateConfig(copy, view);
    if (*status_code != ScryBootstrapSuccess)
    {
        HeapFree(GetProcessHeap(), 0, copy);
        return nullptr;
    }

    return copy;
}

void WriteStatusFile(const WCHAR* path, DWORD* publish_error)
{
    if (path == nullptr)
    {
        return;
    }

    const ScryBootstrapStatusRecordV1 status = GetStatusSnapshot();
    HANDLE file = CreateFileW(
        path,
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        *publish_error = GetLastError();
        return;
    }

    DWORD written = 0;
    const BOOL write_succeeded = WriteFile(
        file,
        &status,
        sizeof(status),
        &written,
        nullptr);
    if (!write_succeeded || written != sizeof(status))
    {
        *publish_error =
            write_succeeded ? ERROR_WRITE_FAULT : GetLastError();
    }
    else if (!FlushFileBuffers(file))
    {
        *publish_error = GetLastError();
    }

    CloseHandle(file);
}

DWORD Complete(
    const ConfigView& view,
    ScryBootstrapStatusCode status_code,
    int32_t runtime_result,
    DWORD win32_error,
    int32_t managed_result,
    const WCHAR* message)
{
    HANDLE completion_event = nullptr;
    DWORD publish_error = 0;
    if (view.completion_event_name != nullptr)
    {
        completion_event = OpenEventW(
            EVENT_MODIFY_STATE,
            FALSE,
            view.completion_event_name);
        if (completion_event == nullptr)
        {
            publish_error = GetLastError();
        }
    }

    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageCompleted,
        status_code,
        runtime_result,
        win32_error != 0 ? win32_error : publish_error,
        managed_result,
        message);

    WriteStatusFile(view.status_path, &publish_error);
    if (publish_error != 0 && win32_error == 0)
    {
        SetStatus(
            view.config->runtime_family,
            ScryBootstrapStageCompleted,
            status_code,
            runtime_result,
            publish_error,
            managed_result,
            message);
    }

    if (completion_event != nullptr)
    {
        if (!SetEvent(completion_event))
        {
            publish_error = GetLastError();
            if (win32_error == 0)
            {
                SetStatus(
                    view.config->runtime_family,
                    ScryBootstrapStageCompleted,
                    status_code,
                    runtime_result,
                    publish_error,
                    managed_result,
                    message);
                DWORD ignored = 0;
                WriteStatusFile(view.status_path, &ignored);
            }
        }
        CloseHandle(completion_event);
    }

    return static_cast<DWORD>(status_code);
}

typedef HRESULT(STDAPICALLTYPE* clr_create_instance_fn)(
    REFCLSID class_id,
    REFIID interface_id,
    LPVOID* interface_pointer);

DWORD StartNetFramework(const ConfigView& view)
{
    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageLocatingRuntime,
        ScryBootstrapSuccess,
        0,
        0,
        0,
        L"Locating the loaded CLR v4 runtime.");

    if (GetModuleHandleW(L"clr.dll") == nullptr)
    {
        return Complete(
            view,
            ScryBootstrapRuntimeNotLoaded,
            0,
            ERROR_MOD_NOT_FOUND,
            0,
            L"CLR v4 is not loaded in the target process.");
    }

    HMODULE mscoree = LoadLibraryExW(
        L"mscoree.dll",
        nullptr,
        LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (mscoree == nullptr)
    {
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            0,
            GetLastError(),
            0,
            L"mscoree.dll could not be loaded.");
    }

    const clr_create_instance_fn create_instance =
        reinterpret_cast<clr_create_instance_fn>(
            GetProcAddress(mscoree, "CLRCreateInstance"));
    if (create_instance == nullptr)
    {
        const DWORD error = GetLastError();
        FreeLibrary(mscoree);
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            0,
            error,
            0,
            L"CLRCreateInstance could not be resolved.");
    }

    ICLRMetaHost* meta_host = nullptr;
    HRESULT hr = create_instance(
        CLSID_CLRMetaHost,
        IID_ICLRMetaHost,
        reinterpret_cast<void**>(&meta_host));
    if (FAILED(hr))
    {
        FreeLibrary(mscoree);
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            hr,
            0,
            0,
            L"ICLRMetaHost could not be created.");
    }

    IEnumUnknown* runtimes = nullptr;
    hr = meta_host->EnumerateLoadedRuntimes(GetCurrentProcess(), &runtimes);
    if (FAILED(hr))
    {
        meta_host->Release();
        FreeLibrary(mscoree);
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            hr,
            0,
            0,
            L"Loaded CLR runtimes could not be enumerated.");
    }

    ICLRRuntimeInfo* selected_runtime = nullptr;
    IUnknown* candidate = nullptr;
    while (runtimes->Next(1, &candidate, nullptr) == S_OK)
    {
        ICLRRuntimeInfo* runtime = nullptr;
        hr = candidate->QueryInterface(
            IID_ICLRRuntimeInfo,
            reinterpret_cast<void**>(&runtime));
        candidate->Release();
        candidate = nullptr;
        if (FAILED(hr))
        {
            continue;
        }

        WCHAR version[64] = {};
        DWORD version_length = ARRAYSIZE(version);
        BOOL loaded = FALSE;
        const HRESULT version_hr =
            runtime->GetVersionString(version, &version_length);
        const HRESULT loaded_hr =
            runtime->IsLoaded(GetCurrentProcess(), &loaded);
        if (SUCCEEDED(version_hr) &&
            SUCCEEDED(loaded_hr) &&
            loaded &&
            version[0] == L'v' &&
            version[1] == L'4' &&
            version[2] == L'.')
        {
            selected_runtime = runtime;
            break;
        }

        runtime->Release();
    }

    runtimes->Release();
    meta_host->Release();
    if (selected_runtime == nullptr)
    {
        FreeLibrary(mscoree);
        return Complete(
            view,
            ScryBootstrapRuntimeNotCompatible,
            0,
            0,
            0,
            L"No loaded CLR v4 runtime was found.");
    }

    ICLRRuntimeHost* runtime_host = nullptr;
    hr = selected_runtime->GetInterface(
        CLSID_CLRRuntimeHost,
        IID_ICLRRuntimeHost,
        reinterpret_cast<void**>(&runtime_host));
    selected_runtime->Release();
    if (FAILED(hr))
    {
        FreeLibrary(mscoree);
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            hr,
            0,
            0,
            L"ICLRRuntimeHost could not be obtained from loaded CLR v4.");
    }

    WCHAR* argument = nullptr;
    if (view.argument_length != 0)
    {
        const int character_count = MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            reinterpret_cast<LPCCH>(view.argument),
            static_cast<int>(view.argument_length),
            nullptr,
            0);
        if (character_count == 0)
        {
            const DWORD error = GetLastError();
            runtime_host->Release();
            FreeLibrary(mscoree);
            return Complete(
                view,
                ScryBootstrapInvalidConfiguration,
                0,
                error,
                0,
                L"The managed argument is not valid UTF-8.");
        }

        argument = static_cast<WCHAR*>(
            HeapAlloc(
                GetProcessHeap(),
                HEAP_ZERO_MEMORY,
                (static_cast<size_t>(character_count) + 1) * sizeof(WCHAR)));
        if (argument == nullptr)
        {
            runtime_host->Release();
            FreeLibrary(mscoree);
            return Complete(
                view,
                ScryBootstrapOutOfMemory,
                0,
                ERROR_OUTOFMEMORY,
                0,
                L"The .NET Framework argument could not be allocated.");
        }

        if (MultiByteToWideChar(
                CP_UTF8,
                MB_ERR_INVALID_CHARS,
                reinterpret_cast<LPCCH>(view.argument),
                static_cast<int>(view.argument_length),
                argument,
                character_count) != character_count)
        {
            const DWORD error = GetLastError();
            HeapFree(GetProcessHeap(), 0, argument);
            runtime_host->Release();
            FreeLibrary(mscoree);
            return Complete(
                view,
                ScryBootstrapInvalidConfiguration,
                0,
                error,
                0,
                L"The managed argument could not be converted from UTF-8.");
        }
    }

    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageCallingManagedEntryPoint,
        ScryBootstrapSuccess,
        0,
        0,
        0,
        L"Calling ExecuteInDefaultAppDomain on the loaded CLR v4.");

    DWORD managed_result = 0;
    hr = runtime_host->ExecuteInDefaultAppDomain(
        view.assembly_path,
        view.type_name,
        view.method_name,
        argument != nullptr ? argument : L"",
        &managed_result);

    if (argument != nullptr)
    {
        HeapFree(GetProcessHeap(), 0, argument);
    }
    runtime_host->Release();
    FreeLibrary(mscoree);

    if (FAILED(hr))
    {
        return Complete(
            view,
            ScryBootstrapManagedAssemblyLoadFailed,
            hr,
            0,
            static_cast<int32_t>(managed_result),
            L"ExecuteInDefaultAppDomain failed.");
    }

    if (managed_result != 0)
    {
        return Complete(
            view,
            ScryBootstrapManagedEntryPointFailed,
            hr,
            0,
            static_cast<int32_t>(managed_result),
            L"The managed .NET Framework entry point returned an error.");
    }

    return Complete(
        view,
        ScryBootstrapSuccess,
        hr,
        0,
        0,
        L"The managed .NET Framework entry point completed.");
}

typedef void* hostfxr_handle;

struct get_hostfxr_parameters
{
    size_t size;
    const WCHAR* assembly_path;
    const WCHAR* dotnet_root;
};

enum hostfxr_delegate_type
{
    hdt_load_assembly_and_get_function_pointer = 5
};

typedef int32_t(__cdecl* get_hostfxr_path_fn)(
    WCHAR* buffer,
    size_t* buffer_size,
    const get_hostfxr_parameters* parameters);
typedef int32_t(__cdecl* hostfxr_initialize_for_runtime_config_fn)(
    const WCHAR* runtime_config_path,
    const void* parameters,
    hostfxr_handle* host_context_handle);
typedef int32_t(__cdecl* hostfxr_get_runtime_delegate_fn)(
    hostfxr_handle host_context_handle,
    hostfxr_delegate_type type,
    void** delegate);
typedef int32_t(__cdecl* hostfxr_close_fn)(
    hostfxr_handle host_context_handle);
typedef int32_t(__stdcall* load_assembly_and_get_function_pointer_fn)(
    const WCHAR* assembly_path,
    const WCHAR* type_name,
    const WCHAR* method_name,
    const WCHAR* delegate_type_name,
    void* reserved,
    void** delegate);
typedef int32_t(__stdcall* managed_entry_point_fn)(
    const void* argument,
    int32_t argument_length);

HMODULE LoadHostFxr(const ConfigView& view, bool* owns_module)
{
    *owns_module = false;
    HMODULE hostfxr = GetModuleHandleW(L"hostfxr.dll");
    if (hostfxr != nullptr)
    {
        return hostfxr;
    }

    if (view.hostfxr_path != nullptr)
    {
        hostfxr = LoadLibraryExW(
            view.hostfxr_path,
            nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (hostfxr != nullptr)
        {
            *owns_module = true;
        }
        return hostfxr;
    }

    bool owns_nethost = false;
    HMODULE nethost = GetModuleHandleW(L"nethost.dll");
    if (nethost == nullptr)
    {
        nethost = LoadLibraryExW(
            L"nethost.dll",
            nullptr,
            LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        owns_nethost = nethost != nullptr;
    }

    if (nethost == nullptr)
    {
        return nullptr;
    }

    const get_hostfxr_path_fn get_hostfxr_path =
        reinterpret_cast<get_hostfxr_path_fn>(
            GetProcAddress(nethost, "get_hostfxr_path"));
    if (get_hostfxr_path == nullptr)
    {
        if (owns_nethost)
        {
            FreeLibrary(nethost);
        }
        return nullptr;
    }

    WCHAR hostfxr_path[32768] = {};
    size_t hostfxr_path_size = ARRAYSIZE(hostfxr_path);
    get_hostfxr_parameters parameters = {};
    parameters.size = sizeof(parameters);
    parameters.assembly_path = view.assembly_path;
    const int32_t result =
        get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    if (owns_nethost)
    {
        FreeLibrary(nethost);
    }
    if (result != 0)
    {
        return nullptr;
    }

    hostfxr = LoadLibraryExW(
        hostfxr_path,
        nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
            LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (hostfxr != nullptr)
    {
        *owns_module = true;
    }
    return hostfxr;
}

DWORD StartModernDotNet(const ConfigView& view)
{
    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageLocatingRuntime,
        ScryBootstrapSuccess,
        0,
        0,
        0,
        L"Locating hostfxr for the loaded CoreCLR runtime.");

    if (GetModuleHandleW(L"coreclr.dll") == nullptr)
    {
        return Complete(
            view,
            ScryBootstrapRuntimeNotLoaded,
            0,
            ERROR_MOD_NOT_FOUND,
            0,
            L"CoreCLR is not loaded in the target process.");
    }

    bool owns_hostfxr = false;
    HMODULE hostfxr = LoadHostFxr(view, &owns_hostfxr);
    if (hostfxr == nullptr)
    {
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            0,
            GetLastError(),
            0,
            L"hostfxr.dll could not be located or loaded.");
    }

    const hostfxr_initialize_for_runtime_config_fn initialize =
        reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
            GetProcAddress(
                hostfxr,
                "hostfxr_initialize_for_runtime_config"));
    const hostfxr_get_runtime_delegate_fn get_delegate =
        reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
            GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate"));
    const hostfxr_close_fn close =
        reinterpret_cast<hostfxr_close_fn>(
            GetProcAddress(hostfxr, "hostfxr_close"));
    if (initialize == nullptr || get_delegate == nullptr || close == nullptr)
    {
        const DWORD error = GetLastError();
        if (owns_hostfxr)
        {
            FreeLibrary(hostfxr);
        }
        return Complete(
            view,
            ScryBootstrapRuntimeHostUnavailable,
            0,
            error,
            0,
            L"Required hostfxr exports could not be resolved.");
    }

    hostfxr_handle context = nullptr;
    int32_t runtime_result =
        initialize(view.runtime_config_path, nullptr, &context);
    if (runtime_result < 0 || context == nullptr)
    {
        if (context != nullptr)
        {
            close(context);
        }
        if (owns_hostfxr)
        {
            FreeLibrary(hostfxr);
        }
        return Complete(
            view,
            ScryBootstrapRuntimeNotCompatible,
            runtime_result,
            0,
            0,
            L"The runtimeconfig is not compatible with the loaded CoreCLR.");
    }

    void* load_assembly_pointer = nullptr;
    runtime_result = get_delegate(
        context,
        hdt_load_assembly_and_get_function_pointer,
        &load_assembly_pointer);
    close(context);
    if (runtime_result < 0 || load_assembly_pointer == nullptr)
    {
        if (owns_hostfxr)
        {
            FreeLibrary(hostfxr);
        }
        return Complete(
            view,
            ScryBootstrapRuntimeDelegateFailed,
            runtime_result,
            0,
            0,
            L"The CoreCLR assembly-loading delegate could not be obtained.");
    }

    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageLoadingManagedAssembly,
        ScryBootstrapSuccess,
        runtime_result,
        0,
        0,
        L"Loading the managed assembly through CoreCLR.");

    const load_assembly_and_get_function_pointer_fn load_assembly =
        reinterpret_cast<load_assembly_and_get_function_pointer_fn>(
            load_assembly_pointer);
    void* managed_entry_pointer = nullptr;
    const WCHAR* unmanaged_callers_only_method =
        reinterpret_cast<const WCHAR*>(static_cast<intptr_t>(-1));
    runtime_result = load_assembly(
        view.assembly_path,
        view.type_name,
        view.method_name,
        unmanaged_callers_only_method,
        nullptr,
        &managed_entry_pointer);
    if (runtime_result < 0 || managed_entry_pointer == nullptr)
    {
        if (owns_hostfxr)
        {
            FreeLibrary(hostfxr);
        }
        return Complete(
            view,
            ScryBootstrapManagedAssemblyLoadFailed,
            runtime_result,
            0,
            0,
            L"The managed CoreCLR entry point could not be loaded.");
    }

    SetStatus(
        view.config->runtime_family,
        ScryBootstrapStageCallingManagedEntryPoint,
        ScryBootstrapSuccess,
        runtime_result,
        0,
        0,
        L"Calling the UnmanagedCallersOnly managed entry point.");

    const managed_entry_point_fn managed_entry =
        reinterpret_cast<managed_entry_point_fn>(managed_entry_pointer);
    const int32_t managed_result = managed_entry(
        view.argument,
        static_cast<int32_t>(view.argument_length));
    if (owns_hostfxr)
    {
        FreeLibrary(hostfxr);
    }

    if (managed_result != 0)
    {
        return Complete(
            view,
            ScryBootstrapManagedEntryPointFailed,
            runtime_result,
            0,
            managed_result,
            L"The managed CoreCLR entry point returned an error.");
    }

    return Complete(
        view,
        ScryBootstrapSuccess,
        runtime_result,
        0,
        managed_result,
        L"The managed CoreCLR entry point completed.");
}
}

extern "C" DWORD WINAPI ScryBootstrapStart(LPVOID configuration)
{
    if (InterlockedCompareExchange(&g_started, 0, 0) != 0)
    {
        return ScryBootstrapAlreadyStarted;
    }

    SetStatus(
        0,
        ScryBootstrapStageValidatingConfiguration,
        ScryBootstrapSuccess,
        0,
        0,
        0,
        L"Validating the native bootstrap configuration.");

    ConfigView view = {};
    ScryBootstrapStatusCode status_code = ScryBootstrapInvalidArgument;
    BYTE* config_copy =
        CopyAndValidateConfig(configuration, &view, &status_code);
    if (config_copy == nullptr)
    {
        SetStatus(
            0,
            ScryBootstrapStageCompleted,
            status_code,
            0,
            0,
            0,
            L"The native bootstrap configuration is invalid.");
        return status_code;
    }

    if (InterlockedCompareExchange(&g_started, 1, 0) != 0)
    {
        HeapFree(GetProcessHeap(), 0, config_copy);
        return ScryBootstrapAlreadyStarted;
    }

    DWORD result = ScryBootstrapInternalError;
    if (view.config->runtime_family == ScryBootstrapRuntimeNetFramework)
    {
        result = StartNetFramework(view);
    }
    else
    {
        result = StartModernDotNet(view);
    }

    HeapFree(GetProcessHeap(), 0, config_copy);
    return result;
}

extern "C" DWORD WINAPI ScryBootstrapGetStatus(LPVOID status_record)
{
    if (status_record == nullptr)
    {
        return ScryBootstrapInvalidArgument;
    }

    uint32_t size = 0;
    uint32_t version = 0;
    __try
    {
        const ScryBootstrapStatusRecordV1* request =
            static_cast<const ScryBootstrapStatusRecordV1*>(status_record);
        size = request->size;
        version = request->version;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return ScryBootstrapInvalidArgument;
    }

    if (version != SCRY_BOOTSTRAP_VERSION)
    {
        return ScryBootstrapUnsupportedConfigurationVersion;
    }
    if (size < sizeof(ScryBootstrapStatusRecordV1))
    {
        return ScryBootstrapInvalidConfiguration;
    }

    const ScryBootstrapStatusRecordV1 status = GetStatusSnapshot();
    __try
    {
        CopyMemory(status_record, &status, sizeof(status));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return ScryBootstrapInvalidArgument;
    }

    return ScryBootstrapSuccess;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
