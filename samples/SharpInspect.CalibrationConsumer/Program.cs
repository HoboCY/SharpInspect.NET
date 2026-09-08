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
                "[--alarm-policy <json>] [--checkerboard-images <directory>] " +
                "[--planar-images <directory>] --user-name <name> " +
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
                var planar = arguments.PlanarImagesDirectory is not null;
                Console.WriteLine(arguments.Mode == CalibrationConsumerMode.Restart &&
                    planar
                    ? "V126-N02 planar-homography-restart PASS schema=14 " +
                      "readOnly=true devices=0 ready=false"
                    : arguments.Mode == CalibrationConsumerMode.Restart &&
                    arguments.CheckerboardImagesDirectory is not null
                    ? "V125-N02 checkerboard-calibration-restart PASS schema=14 " +
                      "readOnly=true devices=0 ready=false"
                    : arguments.Mode == CalibrationConsumerMode.Restart
                    ? "V124-N02 calibration-session-restart PASS schema=14 readOnly=true devices=0 ready=false"
                    : arguments.Mode == CalibrationConsumerMode.All
                        && planar
                        ? "V126-N03 planar-homography-all PASS run=true restart=true ready=false"
                    : arguments.Mode == CalibrationConsumerMode.All
                        && arguments.CheckerboardImagesDirectory is not null
                        ? "V125-N03 checkerboard-calibration-all PASS run=true restart=true ready=false"
                    : arguments.Mode == CalibrationConsumerMode.All
                        ? "V124-N03 calibration-session-all PASS run=true restart=true ready=false"
                    : arguments.CheckerboardImagesDirectory is not null
                        ? "V125-N01 checkerboard-calibration-consumer PASS schema=14 " +
                          "frames=20 observations=20 candidate=true restored=true ready=false"
                    : planar
                        ? "V126-N01 planar-homography-consumer PASS schema=14 " +
                          "frames=2 observations=2 selected=1 candidate=true restored=true ready=false"
                        : "V124-N01 calibration-session-consumer PASS schema=14 fixture=true " +
                          "frames=3 observations=3 candidate=true restored=true ready=false");
            }
            catch (CalibrationConsumer.CalibrationConsumerCheckException exception)
            {
                exitCode = 1;
                var label = arguments.PlanarImagesDirectory is not null
                    ? "V126 planar-homography"
                    : arguments.CheckerboardImagesDirectory is not null
                    ? "V125 checkerboard-calibration" : "V124 calibration-session";
                Console.Error.WriteLine(label + " FAIL reason=" + exception.ReasonCode);
            }
            catch (Exception exception)
            {
                exitCode = 1;
                var label = arguments.PlanarImagesDirectory is not null
                    ? "V126 planar-homography"
                    : arguments.CheckerboardImagesDirectory is not null
                    ? "V125 checkerboard-calibration" : "V124 calibration-session";
                Console.Error.WriteLine(label + " FAIL reason=" + exception.GetType().Name);
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
