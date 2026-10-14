using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ScreenCord.Media;

/// <summary>Inicialização do Media Foundation (uma vez por processo) e utilidades para amostras.</summary>
internal static class MediaFoundation
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFactory.MFStartup(true).CheckError();
        return true;
    });

    public static void EnsureStarted() => _ = Started.Value;

    /// <summary>
    /// Referência própria a um item de <see cref="IMFActivateCollection"/>: liberar a coleção libera os itens dela.
    /// </summary>
    public static IMFActivate Keep(IMFActivate activate)
    {
        Marshal.AddRef(activate.NativePointer);
        return new IMFActivate(activate.NativePointer);
    }

    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time100ns, long duration100ns)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                unsafe
                {
                    data.CopyTo(new Span<byte>((void*)pointer, data.Length));
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = data.Length;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time100ns;
            sample.SampleDuration = duration100ns;
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public static IMFSample CreateEmptySample(int size, int alignment)
    {
        var buffer = alignment > 1
            ? MediaFactory.MFCreateAlignedMemoryBuffer(size, alignment - 1)
            : MediaFactory.MFCreateMemoryBuffer(size);
        try
        {
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Copia o conteúdo da amostra (todos os buffers, em sequência) para um array novo.</summary>
    public static byte[] ToArray(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
            return data;
        }
        finally
        {
            buffer.Unlock();
        }
    }
}

/// <summary>
/// ICodecAPI (configuração de encoders/decoders do Media Foundation), chamado direto pela vtable porque
/// o Vortice não o expõe. Falhas são ignoradas: cada encoder aceita um subconjunto das propriedades.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;

    private static readonly Guid Iid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private IntPtr _pointer;

    private CodecApi(IntPtr pointer)
    {
        _pointer = pointer;
    }

    public static CodecApi? From(ComObject transform)
    {
        return Marshal.QueryInterface(transform.NativePointer, in Iid, out var pointer) == 0 ? new CodecApi(pointer) : null;
    }

    public bool Set(Guid property, uint value) => SetVariant(property, VtUi4, value);

    public bool Set(Guid property, bool value) => SetVariant(property, VtBool, value ? 0xFFFFu : 0u);

    public void Dispose()
    {
        if (_pointer != IntPtr.Zero)
        {
            Marshal.Release(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;

    private bool SetVariant(Guid property, ushort type, uint value)
    {
        if (_pointer == IntPtr.Zero)
        {
            return false;
        }

        // VARIANT: vt (2 bytes) + 6 reservados + valor (16 bytes no x64).
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = type;
        if (type == VtBool)
        {
            *(short*)(variant + 8) = (short)(value != 0 ? -1 : 0);
        }
        else
        {
            *(uint*)(variant + 8) = value;
        }

        // ICodecAPI: [3] IsSupported, [4] IsModifiable, [5] GetParameterRange, [6] GetParameterValues,
        // [7] GetDefaultValue, [8] GetValue, [9] SetValue.
        var vtable = *(IntPtr**)_pointer;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, void*, int>)vtable[9];
        return setValue(_pointer, &property, variant) >= 0;
    }
}
