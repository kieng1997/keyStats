using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Models;
using KeyStats.Services;
using KeyStats.ViewModels;
// Public-boundary test for StatsPopupViewModel's app-list projection. The production view model, its
// base class, the refresher and the models are compiled unchanged; only the external boundaries
// (StatsManager singleton, icon lookup, composition root, localization resource) are stand-ins. The
// test drives the public constructor, the public AppStatsItems collection and the production
// StatsChanged notification, then checks two user-visible properties: the popup's UI thread stays
// responsive while icons are being resolved, and the final list carries the expected text and a
// frozen icon per row. Never starts KeyStats or touches user statistics.
namespace KeyStats.Views
{
    internal static class StatsPopupProjectionTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static int Main()
        {
            try
            {
                RunMissingIcon();
                Console.WriteLine("PASS: missing icon still shows a complete row");
                RunResponsiveness();
                Console.WriteLine("PASS: icon lookup does not block the popup UI thread");
                Console.WriteLine("PASS: popup app-list projection");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// A blocked icon lookup must not freeze the popup's UI thread, and the finished list must carry
        /// the expected text plus a frozen icon per row.
        /// </summary>
        private static void RunResponsiveness()
        {
            Run(blockFirstLookup: true, missingIconFor: "gamma", requireFrozenIcons: true);
        }

        /// <summary>
        /// An app whose icon cannot be resolved still gets a complete row and the popup keeps working.
        /// This is behavior the popup already had before the projection change.
        /// </summary>
        private static void RunMissingIcon()
        {
            Run(blockFirstLookup: false, missingIconFor: "gamma", requireFrozenIcons: false);
        }

        private static void Run(bool blockFirstLookup, string missingIconFor, bool requireFrozenIcons)
        {
            StatsManager.Source = BuildSource();
            AppIconStub.Reset();
            AppIconStub.BlockFirstLookup = blockFirstLookup;
            // The named app has no resolvable icon: its row must still show complete text.
            AppIconStub.Missing.Add(missingIconFor);

            var dispatcherReady = new ManualResetEventSlim(false);
            var populated = new ManualResetEventSlim(false);
            Dispatcher? dispatcher = null;
            StatsPopupViewModel? viewModel = null;
            Exception? uiFailure = null;
            var uiThread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    viewModel = new StatsPopupViewModel();
                    ((INotifyCollectionChanged)viewModel.AppStatsItems).CollectionChanged += (_, _) =>
                    {
                        if (viewModel!.AppStatsItems.Count == 3) populated.Set();
                    };
                }
                catch (Exception ex)
                {
                    uiFailure = ex;
                }
                finally
                {
                    dispatcherReady.Set();
                }

                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "StatsPopupProjectionTest UI"
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            try
            {
                Check(dispatcherReady.Wait(Timeout), "the popup view model was never constructed");
                if (uiFailure != null) throw new Exception("view model construction failed: " + uiFailure.Message);

                // The production view model resolves an icon per app row. Wait until the first lookup is
                // actually running, then check the UI thread still services work.
                if (blockFirstLookup)
                {
                    Check(
                        AppIconStub.FirstLookupStarted.Wait(Timeout),
                        "no app icon lookup ever started, so the popup never projected its app list");

                    var marker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    dispatcher!.BeginInvoke(DispatcherPriority.Normal, new Action(() => marker.TrySetResult(true)));
                    var responsive = marker.Task.Wait(TimeSpan.FromSeconds(2));

                    // Let the blocked lookup finish either way so the run can report and exit.
                    AppIconStub.Release.Set();

                    Check(responsive, "the popup UI thread was blocked while an app icon lookup was running");
                }

                Check(populated.Wait(Timeout), "the app list never received its rows");

                var rows = dispatcher.Invoke(() => viewModel!.AppStatsItems
                    .Select(item => (item.Name, item.KeyPresses, item.Clicks, item.Icon))
                    .ToList());

                Check(rows.Count == 3, $"expected 3 app rows, got {rows.Count}");
                Check(rows[0].Name == "Alpha", $"row 0 name was '{rows[0].Name}'");
                Check(rows[0].KeyPresses == "742", $"row 0 key presses were '{rows[0].KeyPresses}', expected '742'");
                Check(rows[0].Clicks == "31", $"row 0 clicks were '{rows[0].Clicks}', expected '31'");
                Check(rows[1].Name == "Beta", $"row 1 name was '{rows[1].Name}'");
                Check(rows[1].KeyPresses == "12", $"row 1 key presses were '{rows[1].KeyPresses}', expected '12'");
                Check(rows[2].Name == "Gamma", $"row 2 name was '{rows[2].Name}'");
                Check(rows[2].Clicks == "9", $"row 2 clicks were '{rows[2].Clicks}', expected '9'");
                if (requireFrozenIcons)
                {
                    Check(rows[0].Icon != null && rows[0].Icon!.IsFrozen, "row 'Alpha' icon is missing or not frozen");
                    Check(rows[1].Icon != null && rows[1].Icon!.IsFrozen, "row 'Beta' icon is missing or not frozen");
                }

                Check(rows[2].Icon == null, "row 'Gamma' should have no icon, so the missing-icon path is covered");
            }
            finally
            {
                AppIconStub.Release.Set();
                if (dispatcher != null && viewModel != null && !dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        // Production cleanup unsubscribes StatsChanged and closes the refresher.
                        dispatcher.Invoke(new Action(() => viewModel.Cleanup()), TimeSpan.FromSeconds(5));
                    }
                    catch (Exception)
                    {
                        // A shutdown dispatcher cannot run cleanup; the thread exit check below reports it.
                    }
                }

                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
                if (!uiThread.Join(TimeSpan.FromSeconds(10)))
                {
                    throw new Exception("the test UI thread did not exit");
                }
            }
        }

        private static DailyStatsStatsSource BuildSource()
        {
            var apps = new List<AppStats>
            {
                new("alpha", "Alpha") { KeyPresses = 742, LeftClicks = 31 },
                new("beta", "Beta") { KeyPresses = 12, LeftClicks = 5 },
                new("gamma", "Gamma") { KeyPresses = 0, LeftClicks = 9 }
            };

            var today = new DailyStats(new DateTime(2026, 10, 30))
            {
                KeyPresses = 754,
                LeftClicks = 45
            };

            return new DailyStatsStatsSource
            {
                Today = today,
                Apps = apps,
                History = new StatsManager.HistoryTrendSeries(
                    new List<(DateTime, double)> { (new DateTime(2026, 10, 30), 754) },
                    null),
                KeyBreakdown = new List<(string, int)> { ("A", 700), ("B", 54) }
            };
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
