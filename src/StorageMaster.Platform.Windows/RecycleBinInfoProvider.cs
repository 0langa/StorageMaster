using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

public sealed class RecycleBinInfoProvider : IRecycleBinInfoProvider
{
    private readonly ILogger<RecycleBinInfoProvider> _logger;

    public RecycleBinInfoProvider(ILogger<RecycleBinInfoProvider> logger) => _logger = logger;

    public RecycleBinInfo GetRecycleBinInfo()
    {
        var info = new Shell32Interop.SHQUERYRBINFO
        {
            cbSize = Marshal.SizeOf<Shell32Interop.SHQUERYRBINFO>()
        };

        int hr = Shell32Interop.SHQueryRecycleBin(null, ref info);
        if (hr != 0)
        {
            _logger.LogWarning("SHQueryRecycleBin returned 0x{Hr:X8}", hr);
            return new RecycleBinInfo(0, 0);
        }

        return new RecycleBinInfo(info.i64Size, (int)info.i64NumItems);
    }
}
