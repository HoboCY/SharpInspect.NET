using System.Windows;

namespace SharpInspect.CalibrationConsumer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!CalibrationConsumerArguments.TryParse(args, out var arguments, out var error))
        {
            Console.Error.WriteLine("Calibration consumer arguments invalid: " + error);
            Console.Error.WriteLine("Usage: --mode bootstrap|run|restart|wpf|all --directory <path> " +
                "[--trace-db <path>] [--evidence-root <path>] [--identity-policy <json>] " +
                "[--alarm-policy <json>] --user-name <name> " +
                "[--display-name <name>] [--expected-principal <guid>]");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                var password = CalibrationConsumer.ReadPassword();
                if (arguments.Mode == CalibrationConsumerMode.Bootstrap)
                {
                    var bootstrapJson = await CalibrationConsumer.BootstrapAsync(arguments, password);
                    Console.WriteLine(bootstrapJson);
                }
                else if (arguments.Mode is CalibrationConsumerMode.Run or CalibrationConsumerMode.Wpf)
                    await CalibrationConsumer.RunAsync(arguments, password, render: true);
                else if (arguments.Mode == CalibrationConsumerMode.Restart)
                    await CalibrationConsumer.RestartAsync(arguments, password);
                else
                {
                    await CalibrationConsumer.RunAsync(arguments, password, render: true);
                    await CalibrationConsumer.RestartAsync(arguments, password);
                }

                if (arguments.Mode == CalibrationConsumerMode.Bootstrap)
                    return;
                Console.WriteLine(arguments.Mode == CalibrationConsumerMode.Restart
                    ? "V124-N02 calibration-session-restart PASS schema=14 readOnly=true devices=0 ready=false"
                    : arguments.Mode == CalibrationConsumerMode.All
                        ? "V124-N03 calibration-session-all PASS run=true restart=true ready=false"
                        : "V124-N01 calibration-session-consumer PASS schema=14 fixture=true " +
                          "frames=3 observations=3 candidate=true restored=true ready=false");
            }
            catch (CalibrationConsumer.CalibrationConsumerCheckException exception)
            {
                exitCode = 1;
                Console.Error.WriteLine("V124 calibration-session FAIL reason=" + exception.ReasonCode);
            }
            catch (Exception exception)
            {
                exitCode = 1;
                Console.Error.WriteLine("V124 calibration-session FAIL reason=" +
                    exception.GetType().Name);
            }
            finally
            {
                app.Shutdown(exitCode);
            }
        };
        app.Run();
        return exitCode;
    }
}
