using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace DFLegacy.Server;

public interface IDropRandomSource
{
    int Next(int exclusiveMaximum);
}

public sealed record GameRandomStatus(
    string PreferredProvider,
    bool CpuSupportsRdSeed,
    bool NativeBridgeLoaded,
    bool RdSeedEnabled,
    long RdSeedSamples,
    long RdSeedUnavailable,
    long RejectedSamples,
    long OperatingSystemSamples,
    long PseudoRandomSamples);

internal delegate bool TryGetUInt64(out ulong value);

public sealed class GameRandomSource : IDropRandomSource
{
    private readonly bool _cpuSupportsRdSeed;
    private readonly bool _nativeBridgeLoaded;
    private readonly bool _rdSeedEnabled;
    private readonly TryGetUInt64 _tryGetRdSeed;
    private readonly Func<int, int> _operatingSystemNext;
    private readonly Func<int, int> _pseudoRandomNext;
    private readonly Func<bool> _rdSeedRuntimeAvailable;
    private long _rdSeedSamples;
    private long _rdSeedUnavailable;
    private long _rejectedSamples;
    private long _operatingSystemSamples;
    private long _pseudoRandomSamples;

    public static GameRandomSource Shared { get; } = new();

    private GameRandomSource()
        : this(
            NativeRdSeed.CpuSupportsRdSeed,
            NativeRdSeed.NativeBridgeLoaded,
            NativeRdSeed.IsAvailable,
            NativeRdSeed.TryGetUInt64,
            RandomNumberGenerator.GetInt32,
            Random.Shared.Next,
            () => NativeRdSeed.RuntimeAvailable)
    {
    }

    internal GameRandomSource(
        bool cpuSupportsRdSeed,
        bool nativeBridgeLoaded,
        bool rdSeedEnabled,
        TryGetUInt64 tryGetRdSeed,
        Func<int, int> operatingSystemNext,
        Func<int, int> pseudoRandomNext,
        Func<bool>? rdSeedRuntimeAvailable = null)
    {
        _cpuSupportsRdSeed = cpuSupportsRdSeed;
        _nativeBridgeLoaded = nativeBridgeLoaded;
        _rdSeedEnabled = rdSeedEnabled;
        _tryGetRdSeed = tryGetRdSeed;
        _operatingSystemNext = operatingSystemNext;
        _pseudoRandomNext = pseudoRandomNext;
        _rdSeedRuntimeAvailable = rdSeedRuntimeAvailable ?? (() => rdSeedEnabled);
    }

    public int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);

        if (_rdSeedEnabled)
        {
            var bound = checked((ulong)exclusiveMaximum);
            var rejectionThreshold = unchecked(0UL - bound) % bound;
            while (true)
            {
                if (!_tryGetRdSeed(out var sample))
                {
                    Interlocked.Increment(ref _rdSeedUnavailable);
                    break;
                }

                if (sample < rejectionThreshold)
                {
                    Interlocked.Increment(ref _rejectedSamples);
                    continue;
                }

                Interlocked.Increment(ref _rdSeedSamples);
                return checked((int)(sample % bound));
            }
        }

        try
        {
            var value = _operatingSystemNext(exclusiveMaximum);
            if (value is < 0 || value >= exclusiveMaximum)
            {
                throw new InvalidOperationException(
                    "The operating-system random provider returned an out-of-range value.");
            }

            Interlocked.Increment(ref _operatingSystemSamples);
            return value;
        }
        catch (Exception exception) when (exception is
            CryptographicException or PlatformNotSupportedException)
        {
            var value = _pseudoRandomNext(exclusiveMaximum);
            if (value is < 0 || value >= exclusiveMaximum)
            {
                throw new InvalidOperationException(
                    "The pseudo-random fallback returned an out-of-range value.");
            }

            Interlocked.Increment(ref _pseudoRandomSamples);
            return value;
        }
    }

    public GameRandomStatus GetStatus()
    {
        var rdSeedEnabled = _rdSeedEnabled && _rdSeedRuntimeAvailable();
        return new GameRandomStatus(
            rdSeedEnabled ? "RDSEED" : "OperatingSystemCSPRNG",
            _cpuSupportsRdSeed,
            _nativeBridgeLoaded,
            rdSeedEnabled,
            Interlocked.Read(ref _rdSeedSamples),
            Interlocked.Read(ref _rdSeedUnavailable),
            Interlocked.Read(ref _rejectedSamples),
            Interlocked.Read(ref _operatingSystemSamples),
            Interlocked.Read(ref _pseudoRandomSamples));
    }
}

internal static class NativeRdSeed
{
    private const uint RetryCount = 8;
    private static readonly NativeRdSeedState State = Probe();
    private static int _runtimeDisabled;

    public static bool CpuSupportsRdSeed => State.CpuSupportsRdSeed;

    public static bool NativeBridgeLoaded => State.NativeBridgeLoaded;

    public static bool IsAvailable => State.RdSeedAvailable;

    public static bool RuntimeAvailable =>
        State.RdSeedAvailable && Volatile.Read(ref _runtimeDisabled) == 0;

    public static bool TryGetUInt64(out ulong value)
    {
        value = 0;
        if (!RuntimeAvailable)
        {
            return false;
        }

        try
        {
            return NativeMethods.RdSeed64(RetryCount, out value) != 0;
        }
        catch (Exception exception) when (IsNativeLoadException(exception))
        {
            Interlocked.Exchange(ref _runtimeDisabled, 1);
            value = 0;
            return false;
        }
    }

    private static NativeRdSeedState Probe()
    {
        var cpuSupportsRdSeed = CpuAdvertisesRdSeed();
        if (!cpuSupportsRdSeed)
        {
            return new NativeRdSeedState(false, false, false);
        }

        try
        {
            var rdSeedAvailable = NativeMethods.RdSeedSupported() != 0;
            return new NativeRdSeedState(true, true, rdSeedAvailable);
        }
        catch (Exception exception) when (IsNativeLoadException(exception))
        {
            return new NativeRdSeedState(true, false, false);
        }
    }

    private static bool CpuAdvertisesRdSeed()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitProcess
            || !X86Base.IsSupported)
        {
            return false;
        }

        var maximumLeaf = X86Base.CpuId(0, 0).Eax;
        if (maximumLeaf < 7)
        {
            return false;
        }

        var featureBits = unchecked((uint)X86Base.CpuId(7, 0).Ebx);
        return (featureBits & (1u << 18)) != 0;
    }

    private static bool IsNativeLoadException(Exception exception) => exception is
        DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException or
        PlatformNotSupportedException or
        SEHException;

    private sealed record NativeRdSeedState(
        bool CpuSupportsRdSeed,
        bool NativeBridgeLoaded,
        bool RdSeedAvailable);

    private static class NativeMethods
    {
        private const string LibraryName = "DFLegacy.RandomNative.dll";

        [DllImport(
            LibraryName,
            EntryPoint = "dflegacy_rdseed_supported",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true)]
        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern int RdSeedSupported();

        [DllImport(
            LibraryName,
            EntryPoint = "dflegacy_rdseed64",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true)]
        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
        internal static extern int RdSeed64(uint retryCount, out ulong value);
    }
}
