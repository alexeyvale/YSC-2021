using Land.Core.Parsing.Tree;
using Land.Markup;
using Land.Markup.Binding;
using Land.Markup.CoreExtension;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Comparison
{
	class Program
	{
		private static string BaseFolder { get; set; }
		private static string ModifiedFolder { get; set; }

		public class GetNodeSequenceVisitor: BaseTreeVisitor
		{
			public List<Node> Sequence = new List<Node>();

			public override void Visit(Node node)
			{
				Sequence.Add(node);
				base.Visit(node);
			}
		}

		/// <summary>
		/// Парсинг набора файлов и получение их АСТ в форматах LanD и Core
		/// </summary>
		static List<ParsedFile> GetSearchArea(
			Land.Core.Parsing.BaseParser landParser,
			List<string> files,
			List<string> landErrors)
		{
			var landSearchArea = new List<ParsedFile>();

			var counter = 0;
			var start = DateTime.Now;

			foreach (var file in files)
			{
				var landParsed = ParseFile(landParser, file, landErrors);

				landSearchArea.Add(landParsed);

				++counter;
				if (counter % 100 == 0)
					Console.WriteLine($"{counter} out of {files.Count}...");
			}

			Console.WriteLine($"LanD parsing done in {DateTime.Now - start}");

			return landSearchArea;
		}

		static ParsedFile ParseFile(
			Land.Core.Parsing.BaseParser landParser,
			string file,
			List<string> landErrors)
		{
			/// Читаем текст из файла
			var text = File.ReadAllText(file);

			/// Парсим при помощи LanD
			var landRoot = landParser.Parse(text);
			if (landParser.Log.Any(l => l.Type == Land.Core.MessageType.Error))
				landErrors.Add(file);

			return new ParsedFile
			{
				Name = Path.GetFileName(file),
				Root = landRoot,
				Text = text
			};
		}

		static void Main(string[] args)
		{
			if (args.Length < 2)
			{
				return;
			}

			BaseFolder = args[0];
			ModifiedFolder = args[1];

			FullBinding();
		}

		static void FullBinding()
		{
			var heuristic = new ContextsEqualityHeuristic();
			var markupManager = new MarkupManager(null, heuristic);
			var entityTypes = new string[] { "class_struct_interface", "method", "field", "property" };

			/// Создаём парсер и менеджер разметки из библиотеки LanD	
			var landParser = sharp_lr.ParserProvider.GetParser(false);
			landParser.SetVisitor(g => new MarkupOptionsProcessingVisitor(g));
			landParser.SetPreprocessor(new SharpPreprocessing.ConditionalCompilation.SharpPreprocessor());

			var landErrors = new List<string>();

			/////////////////////////////////////////////// STAGE 1

			var initialFiles = new HashSet<string>(Directory.GetFiles(BaseFolder, "*.cs"));

			var report = new StreamWriter("report.txt");
			var casesForRandomBinding = new List<string>();

			Dictionary<string, List<List<string>>> sameAutoResult = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				differentAutoResult = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				modifiedOnlyAutoResult = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				basicOnlyAutoResult = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				sameFirstPos = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				differentFirstPos = entityTypes.ToDictionary(e => e, e => new List<List<string>>());

			Dictionary<string, List<List<string>>> modifiedOnlyDifferentFirst = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				modifiedOnlyOverloaded = entityTypes.ToDictionary(e => e, e => new List<List<string>>()),
				identicalCandidates = entityTypes.ToDictionary(e => e, e => new List<List<string>>());

			Dictionary<string, int> simpleRebindSameAuto = entityTypes.ToDictionary(e => e, e => 0),
				simpleRebindDifferentAuto = entityTypes.ToDictionary(e => e, e => 0),
				simpleRebindModifiedAuto = entityTypes.ToDictionary(e => e, e => 0);

			Dictionary<string, int> pointsOfType = entityTypes.ToDictionary(e => e, e => 0);

			var counter = 0;
			var modifiedTime = new TimeSpan();
			var baseTime = new TimeSpan();

			foreach (var file in initialFiles.ToList())
			{
				++counter;
				if(counter % 100 == 0)
				{
					Console.WriteLine($"{counter} out of {initialFiles.Count}...");
				}

				var initialFile = ParseFile(landParser, file, landErrors);
				var currentFile = ParseFile(landParser, Path.Combine(ModifiedFolder, Path.GetFileName(file)), landErrors);

				if(initialFile.Root == null || currentFile.Root == null)
				{
					Console.WriteLine($"{Path.GetFileName(file)} skipped...");
					continue;
				}

				markupManager.AddLand(initialFile);

				markupManager.ContextFinder.UseOldApproach = false;
				var startTime = DateTime.Now;
				var modifiedRemapResult = markupManager.Remap(new List<ParsedFile> { currentFile }, false, ContextFinder.SearchType.Local);
				modifiedTime += DateTime.Now - startTime;

				markupManager.ContextFinder.UseOldApproach = true;
				startTime = DateTime.Now;
				var basicRemapResult = markupManager.Remap(new List<ParsedFile> { currentFile }, false, ContextFinder.SearchType.Local);
				baseTime += DateTime.Now - startTime;

				foreach (var key in entityTypes)
				{
					var currentPointsOfType = modifiedRemapResult.Keys.Where(e => e.Context.Type == key).ToList();

					pointsOfType[key] += currentPointsOfType.Count;

					foreach (var cp in currentPointsOfType)
					{
						var modifiedResult = modifiedRemapResult[cp].Where(e => !e.Deleted).ToList();
						var basicResult = basicRemapResult[cp].Where(e => !e.Deleted).ToList();

						var isModifiedAuto = modifiedResult.FirstOrDefault()?.IsAuto ?? false;
						var isBasicAuto = basicResult.FirstOrDefault()?.IsAuto ?? false;

						var sameFirst = basicResult.Count == 0 && modifiedResult.Count == 0
							|| basicResult.Count > 0 && modifiedResult.Count > 0
								&& modifiedResult.First().Context.HeaderContext.Sequence_old
									.SequenceEqual(basicResult.First().Context.HeaderContext.Sequence_old)
								&& modifiedResult.First().Context.StartOffset == basicResult.First().Context.StartOffset;

						var hasNotChanged = isModifiedAuto && initialFile.Text.Substring(
							cp.AstNode.Location.Start.Offset,
							cp.AstNode.Location.Length.Value
						) == currentFile.Text.Substring(
							modifiedRemapResult[cp][0].Node.Location.Start.Offset,
							modifiedRemapResult[cp][0].Node.Location.Length.Value
						) && cp.Context.AncestorsContext.SequenceEqual(modifiedRemapResult[cp][0].Context.AncestorsContext);

						if (!hasNotChanged)
						{
							//casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");

							var reportLines = new List<string>();

							reportLines.Add($"file:///{BaseFolder}\\{cp.Context.FileName}");
							reportLines.Add($"file:///{ModifiedFolder}\\{cp.Context.FileName}");
							reportLines.Add("*");

							reportLines.Add($"{String.Join(" ", cp.Context.HeaderContext.Sequence_old)}     {cp.Context.Line}");
							reportLines.Add("*");

							foreach (var landCandidate in basicRemapResult[cp].Take(7).OrderBy(r => r.Deleted))
							{
								reportLines.Add($"{String.Join(" ", landCandidate.Context.HeaderContext.Sequence_old)}     {landCandidate.Context.Line}");
								reportLines.Add($"\t{landCandidate.Similarity:0.000}  [HC={landCandidate.HeaderCoreSimilarity:0.00};  H={landCandidate.HeaderNonCoreSimilarity:0.00};  I={landCandidate.InnerSimilarity:0.00};  S={landCandidate.AncestorSimilarity:0.00}] {(landCandidate.IsAuto ? "*" : (landCandidate.Deleted ? "#" : ""))}");
							}

							reportLines.Add("*");

							if (modifiedRemapResult[cp].Count > 0)
							{
								if (modifiedRemapResult[cp][0].Weights != null)
								{
									reportLines.Add($"HC={modifiedRemapResult[cp][0].Weights[ContextType.HeaderCore]};  " +
										$"HNC={modifiedRemapResult[cp][0].Weights[ContextType.HeaderNonCore]:0.00};  " +
										$"I={modifiedRemapResult[cp][0].Weights[ContextType.Inner]:0.00};  " +
										$"S={modifiedRemapResult[cp][0].Weights[ContextType.Ancestors]:0.00};  " +
										$"NN={modifiedRemapResult[cp][0].Weights[ContextType.SiblingsNearest]:0.00};  " +
										$"NA={modifiedRemapResult[cp][0].Weights[ContextType.SiblingsAll]:0.00}]");
								}
							}

							foreach (var landCandidate in modifiedRemapResult[cp].Take(7).OrderBy(r => r.Deleted))
							{
								reportLines.Add($"{String.Join(" ", landCandidate.Context.HeaderContext.Sequence_old)}     {landCandidate.Context.Line}");
								reportLines.Add($"\t{landCandidate.Similarity:0.000}  [HC={landCandidate.HeaderCoreSimilarity:0.00};  HNC={landCandidate.HeaderNonCoreSimilarity:0.00};  " +
									$"I={landCandidate.InnerSimilarity:0.00};  S={landCandidate.AncestorSimilarity:0.00};  " +
									$"NN={landCandidate.SiblingsNearestSimilarity:0.00};  NA={landCandidate.SiblingsAllSimilarity:0.00}] " +
									$"{(landCandidate.IsAuto ? "*" : (landCandidate.Deleted ? "#" : ""))}");
							}
							reportLines.Add("");
							reportLines.Add("**************************************************************");
							reportLines.Add("");

							foreach (var line in reportLines)
							{
								report.WriteLine(line);
							}

							if (modifiedRemapResult[cp].Count > 0 && modifiedRemapResult[cp].Skip(1).Any(e => e.Context.AncestorsContext.SequenceEqual(modifiedRemapResult[cp][0].Context.AncestorsContext)
									&& e.Context.HeaderContext.Core.SequenceEqual(modifiedRemapResult[cp][0].Context.HeaderContext.Core)
									&& e.Context.HeaderContext.NonCore.SequenceEqual(modifiedRemapResult[cp][0].Context.HeaderContext.NonCore)))
							{
								identicalCandidates[key].Add(reportLines);
							}

							if (isModifiedAuto)
							{
								if (isBasicAuto)
								{
									if (sameFirst)
									{
										if (modifiedRemapResult[cp][0].Weights == null)
										{
											simpleRebindSameAuto[key] += 1;
										}
										else
										{
											sameAutoResult[key].Add(reportLines);
											casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");
										}
									}
									else
									{
										if (modifiedRemapResult[cp][0].Weights == null)
										{
											simpleRebindDifferentAuto[key] += 1;
										}
										else
										{
											differentAutoResult[key].Add(reportLines);
											casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");
										}
									}
								}
								else
								{
									if (!sameFirst)
									{
										modifiedOnlyDifferentFirst[key].Add(reportLines);
									}

									if (modifiedRemapResult[cp].Count > 1
										&& modifiedRemapResult[cp].Skip(1).Any(e => e.Context.AncestorsContext.SequenceEqual(modifiedRemapResult[cp][0].Context.AncestorsContext)
											&& e.Context.HeaderContext.Core.SequenceEqual(modifiedRemapResult[cp][0].Context.HeaderContext.Core)))
									{
										modifiedOnlyOverloaded[key].Add(reportLines);
									}

									if (modifiedRemapResult[cp][0].Weights == null)
									{
										simpleRebindModifiedAuto[key] += 1;
									}
									else
									{
										modifiedOnlyAutoResult[key].Add(reportLines);
										casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");
									}
								}
							}
							else if (isBasicAuto)
							{
								basicOnlyAutoResult[key].Add(reportLines);
								//casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");
							}
							else
							{
								//casesForRandomBinding.Add($"{cp.Context.FileName};{cp.Context.Type};{cp.Context.StartOffset}");

								if (sameFirst)
								{
									sameFirstPos[key].Add(reportLines);
								}
								else
								{
									differentFirstPos[key].Add(reportLines);
								}
							}
						}
					}
				}

				markupManager.Clear();
				markupManager.ContextFinder.ContextManager.ClearCache(initialFile.Name);
				markupManager.ContextFinder.ContextManager.ClearCache(currentFile.Name);
			}

			Console.WriteLine();

			foreach (var key in entityTypes)
			{
				File.WriteAllLines($"{key}_basicOnlyAutoResult.txt",
					basicOnlyAutoResult[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_modifiedOnlyAutoResult.txt",
					modifiedOnlyAutoResult[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_sameAutoResult.txt",
					sameAutoResult[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_differentAutoResult.txt",
					differentAutoResult[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_sameFirstPos.txt",
					sameFirstPos[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_differentFirstPos.txt",
					differentFirstPos[key].SelectMany(r => r));

				File.WriteAllLines($"{key}_modifiedOnlyOverloaded.txt",
					modifiedOnlyOverloaded[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_modifiedOnlyDifferentFirst.txt",
					modifiedOnlyDifferentFirst[key].SelectMany(r => r));
				File.WriteAllLines($"{key}_identicalCandidates.txt",
					identicalCandidates[key].SelectMany(r => r));

				if (sameAutoResult[key].Count > 100)
				{

					var randomAutoIdx = new HashSet<int>();
					var random = new Random(7);

					while (randomAutoIdx.Count < 100)
					{
						randomAutoIdx.Add(random.Next(sameAutoResult[key].Count));
					}

					File.WriteAllLines($"{key}_sameAutoResult_toCheck.txt",
						randomAutoIdx.SelectMany(idx => sameAutoResult[key][idx]));
				}

				if (sameFirstPos[key].Count > 100)
				{

					var randomAutoIdx = new HashSet<int>();
					var random = new Random(7);

					while (randomAutoIdx.Count < 100)
					{
						randomAutoIdx.Add(random.Next(sameFirstPos[key].Count));
					}

					File.WriteAllLines($"{key}_sameFirstPos_toCheck.txt",
						randomAutoIdx.SelectMany(idx => sameFirstPos[key][idx]));
				}

				Console.WriteLine($"Total count: {pointsOfType[key]}");
				Console.WriteLine($"Total marked: {simpleRebindModifiedAuto[key] + simpleRebindSameAuto[key] + simpleRebindDifferentAuto[key] + modifiedOnlyAutoResult[key].Count + basicOnlyAutoResult[key].Count + sameAutoResult[key].Count + differentAutoResult[key].Count + sameFirstPos[key].Count + differentFirstPos[key].Count}");
				Console.WriteLine($"Simple rebinding: {simpleRebindModifiedAuto[key] + simpleRebindSameAuto[key] + simpleRebindDifferentAuto[key]}");
				Console.WriteLine($"Modified only auto: {modifiedOnlyAutoResult[key].Count}");
				Console.WriteLine($"Basic only auto: {basicOnlyAutoResult[key].Count}");
				Console.WriteLine($"Same auto: {sameAutoResult[key].Count}");
				Console.WriteLine($"Different auto: {differentAutoResult[key].Count}");
				Console.WriteLine($"Same first: {sameFirstPos[key].Count}");
				Console.WriteLine($"Different first: {differentFirstPos[key].Count}");

				Console.WriteLine($"Overloaded methods when modified auto: {modifiedOnlyOverloaded[key].Count}");
				Console.WriteLine($"Different first when modified auto: {modifiedOnlyDifferentFirst[key].Count}");
				Console.WriteLine($"Identical candidates: {identicalCandidates[key].Count}");

				Console.WriteLine($"'{key}' done!");
				Console.WriteLine();
			}

			Console.WriteLine($"Base time: {baseTime}");
			Console.WriteLine($"Modified time: {modifiedTime}");

			report.Close();

			File.WriteAllLines($"randomBinding.txt", casesForRandomBinding);

			Console.WriteLine("Job's done!");
			Console.ReadLine();
		}
	}
}
