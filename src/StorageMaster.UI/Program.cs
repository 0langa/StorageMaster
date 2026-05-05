using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Interfaces;

namespace StorageMaster.UI;

public static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [STAThread]
    public static void Main(string[] args)
    {
        var isCli = args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase);
        var isHeadless = args.Length > 0 && args[0].Equals("--headless", StringComparison.OrdinalIgnoreCase);

        if (isCli || isHeadless)
        {
            EnsureConsole(isCli);
            var services = ServiceBootstrapper.BuildServices();
            App.SetServices(services);
            var commandArgs = args.Skip(1).ToArray();
            var exitCode = services.GetRequiredService<ICommandRunner>()
                .RunAsync(commandArgs, isHeadless, Console.Out, Console.Error)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        App.SetServices(ServiceBootstrapper.BuildServices());

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static void EnsureConsole(bool allocateWhenMissing)
    {
        if (AttachConsole(AttachParentProcess))
            return;

        if (allocateWhenMissing)
            _ = AllocConsole();
    }
}
