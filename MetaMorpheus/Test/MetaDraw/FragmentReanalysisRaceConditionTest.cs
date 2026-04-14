using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Easy.Common.Extensions;
using EngineLayer;
using EngineLayer.DatabaseLoading;
using GuiFunctions;
using MassSpectrometry;
using MzLibUtil;
using Nett;
using NUnit.Framework;
using Omics.Fragmentation;
using Readers;
using TaskLayer;

namespace Test.MetaDraw
{
    [ExcludeFromCodeCoverage]
    [NonParallelizable]
    internal class FragmentReanalysisRaceConditionTest
    {
        [Test]
        public static void MatchIonsWithNewTypes_ProductsChangedDuringExecution_DoesNotThrow()
        {
            // Load test data similar to TestFragmentationReanalysisViewModel_RematchIons
            var viewModel = new FragmentationReanalysisViewModel();

            // run a quick search 
            var myTomlPath = Path.Combine(TestContext.CurrentContext.TestDirectory, @"TestData\Task1-SearchTaskconfig.toml");
            var searchTaskLoaded = Toml.ReadFile<SearchTask>(myTomlPath, MetaMorpheusTask.tomlConfig);
            string outputFolder = Path.Combine(TestContext.CurrentContext.TestDirectory, @"TestData\TestRaceCondition");
            string myFile = Path.Combine(TestContext.CurrentContext.TestDirectory, @"TestData\TaGe_SA_A549_3_snip.mzML");
            string myDatabase = Path.Combine(TestContext.CurrentContext.TestDirectory, @"TestData\TaGe_SA_A549_3_snip.fasta");
            var engineToml = new EverythingRunnerEngine(new List<(string, MetaMorpheusTask)> { ("SearchTOML", searchTaskLoaded) }, new List<string> { myFile }, new List<DbForTask> { new DbForTask(myDatabase, false) }, outputFolder);
            engineToml.Run();
            string psmFile = Path.Combine(outputFolder, @"SearchTOML\AllPSMs.psmtsv");
            var dataFile = MsDataFileReader.GetDataFile(myFile);

            // parse out psm and its respective scan
            List<PsmFromTsv> parsedPsms = SpectrumMatchTsvReader.ReadPsmTsv(psmFile, out var warnings);
            var psmToResearch = parsedPsms.First();
            var scan = dataFile.GetOneBasedScan(psmToResearch.Ms2ScanNumber);

            // Set up cancellation for concurrent tasks
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            Exception caughtException = null;

            // Modifier spins as fast as possible — no sleep — to maximise the chance of
            // colliding with the enumerator inside MatchIonsWithNewTypes.
            var modifierTask = Task.Run(() =>
            {
                var dummy = new FragmentViewModel(false, ProductType.Y);
                while (!token.IsCancellationRequested)
                {
                    viewModel.PossibleProducts.Add(dummy);
                    viewModel.PossibleProducts.Remove(dummy);
                }
            }, token);

            // Background task that calls MatchIonsWithNewTypes repeatedly
            var matcherTask = Task.Run(() =>
            {
                for (int i = 0; i < 500 && !token.IsCancellationRequested; i++)
                {
                    try
                    {
                        viewModel.MatchIonsWithNewTypes(scan, psmToResearch, true);
                    }
                    catch (InvalidOperationException ex)
                    {
                        caughtException = ex;
                        cts.Cancel();
                        break;
                    }
                }
            }, token);

            try
            {
                // Wait for matcher to finish (or cancel on first exception)
                matcherTask.Wait(TimeSpan.FromSeconds(10));
                cts.Cancel();
                // Wait for modifier to exit its loop
                modifierTask.Wait(TimeSpan.FromSeconds(2));
            }
            finally
            {
                // Always clean up, even if the assertion below fails
                Directory.Delete(outputFolder, true);
            }

            // If an InvalidOperationException was thrown, fail the test
            Assert.That(caughtException, Is.Null, $"InvalidOperationException thrown: {caughtException?.Message}");
        }
    }
}
