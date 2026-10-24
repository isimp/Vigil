#include <windows.h>
#include <stdio.h>

typedef void* (*jit_init_version_t)(const char*, const char*);
typedef void (*set_dirs_t)(const char*, const char*);
typedef void (*set_assemblies_path_t)(const char*);
typedef void* (*domain_assembly_open_t)(void*, const char*);
typedef int (*jit_exec_t)(void*, void*, int, char**);

int main(int argc, char** argv)
{
    if (argc < 5) { printf("usage: host <mono dll> <managed dir> <etc dir> <exe>\n"); return 2; }
    HMODULE mono = LoadLibraryExA(argv[1], NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!mono) { printf("LoadLibrary failed %lu\n", GetLastError()); return 3; }
    set_dirs_t set_dirs = (set_dirs_t)GetProcAddress(mono, "mono_set_dirs");
    set_assemblies_path_t set_path = (set_assemblies_path_t)GetProcAddress(mono, "mono_set_assemblies_path");
    jit_init_version_t init = (jit_init_version_t)GetProcAddress(mono, "mono_jit_init_version");
    domain_assembly_open_t open = (domain_assembly_open_t)GetProcAddress(mono, "mono_domain_assembly_open");
    jit_exec_t exec = (jit_exec_t)GetProcAddress(mono, "mono_jit_exec");
    if (!set_dirs || !set_path || !init || !open || !exec) { printf("missing export\n"); return 4; }
    set_dirs(argv[2], argv[3]);
    set_path(argv[2]);
    void* domain = init("probe", "v4.0.30319");
    if (!domain) { printf("init failed\n"); return 5; }
    void* assembly = open(domain, argv[4]);
    if (!assembly) { printf("open failed\n"); return 6; }
    char* args[1] = { argv[4] };
    return exec(domain, assembly, 1, args);
}
