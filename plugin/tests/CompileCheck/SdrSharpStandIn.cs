// Compile-check only: the parts of the SDR# plugin API (SDRSharp.Common / SDRSharp.Radio, SDK for
// .NET 9, revision 1921/1922) that the plugin uses, with signatures copied from the SDK's metadata.
// Not part of the plugin and never distributed with it.
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SDRSharp.Radio
{
    public struct Complex { public float Real; public float Imag; }
    public enum ProcessorType { RawIQ = 0, DecimatedAndFilteredIQ = 1, DemodulatorOutput = 2, FilteredAudioOutput = 3, FMMPX = 4, RDSBitStream = 5, RawReal = 6, PreIF = 7, PostIF = 8, PreAF = 9, PostAF = 10, MonitorAF = 11 }
    public enum DetectorType { NFM = 0, WFM = 1, AM = 2, DSB = 3, LSB = 4, USB = 5, CW = 6, RAW = 7 }
    public interface IBaseProcessor { bool Enabled { get; set; } }
    public interface IStreamProcessor : IBaseProcessor { double SampleRate { set; } }
    public unsafe interface IIQProcessor : IStreamProcessor, IBaseProcessor { void Process(Complex* buffer, int length); }
    public unsafe interface IRealProcessor : IStreamProcessor, IBaseProcessor { void Process(float* buffer, int length); }
}

namespace SDRSharp.Common
{
    using SDRSharp.Radio;
    public interface ISharpControl
    {
        DetectorType DetectorType { get; set; }
        int FilterBandwidth { get; set; }
        bool SquelchEnabled { get; set; }
        bool ThemeIsDark { get; }
        Color ThemeForeColor { get; }
        Color ThemeBackColor { get; }
        Color ThemePanelColor { get; }
        Color ThemeEditorColor { get; }
        double AudioSampleRate { get; }
        event PropertyChangedEventHandler PropertyChanged;
        void RegisterStreamHook(object streamHook, ProcessorType processorType);
        void UnregisterStreamHook(object streamHook);
    }
    public interface ISharpPlugin { UserControl Gui { get; } string DisplayName { get; } void Initialize(ISharpControl control); void Close(); }
    public interface ICanLazyLoadGui { void LoadGui(); }
    public interface ISupportStatus { bool IsActive { get; } }
    public interface IExtendedNameProvider { string Category { get; } string MenuItemName { get; } }
}
