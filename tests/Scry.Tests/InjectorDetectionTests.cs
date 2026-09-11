#if NET9_0_OR_GREATER
using Scry.Injector;

namespace Scry.Tests;

public sealed class InjectorDetectionTests
{
    [Theory]
    [InlineData(0x014c, 0x8664, TargetArchitecture.X86)]
    [InlineData(0x0000, 0x8664, TargetArchitecture.X64)]
    public void Machine_detection_distinguishes_x86_and_x64(
        ushort processMachine,
        ushort nativeMachine,
        TargetArchitecture expected)
    {
        Assert.Equal(expected, ProcessInspector.ClassifyMachine(processMachine, nativeMachine));
    }

    [Fact]
    public void Machine_detection_rejects_arm64()
    {
        var exception = Assert.Throws<InjectionException>(() =>
            ProcessInspector.ClassifyMachine(0x0000, 0xaa64));

        Assert.Equal(InjectionErrorCode.UnsupportedArchitecture, exception.Code);
    }

    [Fact]
    public void Architecture_mismatch_is_refused_before_injection()
    {
        var exception = Assert.Throws<InjectionException>(() =>
            ProcessInspector.EnsureCompatibleArchitecture(
                TargetArchitecture.X64,
                TargetArchitecture.X86));

        Assert.Equal(InjectionErrorCode.ArchitectureMismatch, exception.Code);
    }

    [Theory]
    [InlineData("clr.dll", TargetRuntimeFamily.NetFramework)]
    [InlineData("coreclr.dll", TargetRuntimeFamily.ModernDotNet)]
    public void Runtime_detection_uses_loaded_clr_module(
        string module,
        TargetRuntimeFamily expected)
    {
        Assert.Equal(expected, ProcessInspector.ClassifyRuntime(["kernel32.dll", module]));
    }

    [Fact]
    public void Runtime_detection_refuses_unmanaged_processes()
    {
        var exception = Assert.Throws<InjectionException>(() =>
            ProcessInspector.ClassifyRuntime(["kernel32.dll", "user32.dll"]));

        Assert.Equal(InjectionErrorCode.UnsupportedClr, exception.Code);
    }

    [Fact]
    public void Runtime_detection_refuses_ambiguous_mixed_clr_processes()
    {
        var exception = Assert.Throws<InjectionException>(() =>
            ProcessInspector.ClassifyRuntime(["clr.dll", "coreclr.dll"]));

        Assert.Equal(InjectionErrorCode.DetectionInconclusive, exception.Code);
    }
}
#endif
