#pragma once

#include <Windows.h>
#include <stdint.h>

#define SCRY_BOOTSTRAP_MAGIC 0x59524353u
#define SCRY_BOOTSTRAP_VERSION 1u
#define SCRY_BOOTSTRAP_MAX_CONFIG_SIZE (1024u * 1024u)

typedef enum ScryBootstrapRuntimeFamily
{
    ScryBootstrapRuntimeNetFramework = 1,
    ScryBootstrapRuntimeModernDotNet = 2
} ScryBootstrapRuntimeFamily;

typedef enum ScryBootstrapStage
{
    ScryBootstrapStageNotStarted = 0,
    ScryBootstrapStageValidatingConfiguration = 1,
    ScryBootstrapStageLocatingRuntime = 2,
    ScryBootstrapStageLoadingManagedAssembly = 3,
    ScryBootstrapStageCallingManagedEntryPoint = 4,
    ScryBootstrapStageCompleted = 5
} ScryBootstrapStage;

typedef enum ScryBootstrapStatusCode
{
    ScryBootstrapSuccess = 0,
    ScryBootstrapInvalidArgument = 0x2001,
    ScryBootstrapInvalidConfiguration = 0x2002,
    ScryBootstrapUnsupportedConfigurationVersion = 0x2003,
    ScryBootstrapAlreadyStarted = 0x2004,
    ScryBootstrapOutOfMemory = 0x2005,
    ScryBootstrapRuntimeNotLoaded = 0x2101,
    ScryBootstrapRuntimeNotCompatible = 0x2102,
    ScryBootstrapRuntimeHostUnavailable = 0x2103,
    ScryBootstrapRuntimeInitializationFailed = 0x2104,
    ScryBootstrapRuntimeDelegateFailed = 0x2105,
    ScryBootstrapManagedAssemblyLoadFailed = 0x2201,
    ScryBootstrapManagedEntryPointFailed = 0x2202,
    ScryBootstrapInternalError = 0x2FFF
} ScryBootstrapStatusCode;

/*
 * All offsets are byte offsets from the start of ScryBootstrapConfigV1.
 * Wide strings use UTF-16 code-unit lengths and must have a trailing NUL
 * inside total_size. argument uses a byte length and contains UTF-8 without
 * a required trailing NUL. Empty optional values have offset == length == 0.
 */
typedef struct ScryBootstrapBufferRef
{
    uint32_t offset;
    uint32_t length;
} ScryBootstrapBufferRef;

/*
 * The .NET Framework entry point must be a public static method with the
 * signature int Method(string). For modern .NET, type_name is the
 * assembly-qualified type name and method_name identifies an
 * UnmanagedCallersOnly method with the signature int Method(void*, int).
 * The modern entry point receives argument as UTF-8 bytes.
 */
typedef struct ScryBootstrapConfigV1
{
    uint32_t magic;
    uint16_t version;
    uint16_t header_size;
    uint32_t total_size;
    uint32_t runtime_family;
    uint32_t flags;
    ScryBootstrapBufferRef assembly_path;
    ScryBootstrapBufferRef runtime_config_path;
    ScryBootstrapBufferRef type_name;
    ScryBootstrapBufferRef method_name;
    ScryBootstrapBufferRef argument;
    ScryBootstrapBufferRef hostfxr_path;
    ScryBootstrapBufferRef status_path;
    ScryBootstrapBufferRef completion_event_name;
    uint32_t reserved[8];
} ScryBootstrapConfigV1;

typedef struct ScryBootstrapStatusRecordV1
{
    uint32_t size;
    uint32_t version;
    uint32_t stage;
    uint32_t status_code;
    int32_t runtime_result;
    uint32_t win32_error;
    int32_t managed_result;
    uint32_t runtime_family;
    uint32_t process_id;
    uint32_t thread_id;
    FILETIME updated_at;
    WCHAR message[256];
} ScryBootstrapStatusRecordV1;

/*
 * Initialize size and version before passing this buffer to
 * ScryBootstrapGetStatus. The remaining fields are output.
 */
#ifdef __cplusplus
extern "C" {
#endif

DWORD WINAPI ScryBootstrapStart(LPVOID configuration);
DWORD WINAPI ScryBootstrapGetStatus(LPVOID status_record);

#ifdef __cplusplus
}
#endif
