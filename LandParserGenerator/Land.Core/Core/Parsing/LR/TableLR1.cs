using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Land.Core.Specification;

namespace Land.Core.Parsing.LR
{
	/// <summary>
	/// Таблица LR(1) парсинга
	/// </summary>
	[Serializable]
	public class TableLR1: BaseTable
	{
		private Dictionary<string, int> Lookaheads { get; set; }

		/// <summary>
		/// Множества состояний (множество множеств пунктов)
		/// </summary>
		public List<HashSet<Marker>> Items { get; set; }
		/// <summary>
		/// Действия, которые надо совершать при встрече различных терминалов
		/// </summary>
		private HashSet<Action>[,] Actions { get; set; }
		/// <summary>
		/// Переходы между состояниями
		/// </summary>
		public List<Dictionary<string, int>> Transitions { get; private set; }

		public TableLR1(Grammar g): base(g)
		{
			Lookaheads = g.Tokens.Keys
				.Zip(Enumerable.Range(0, g.Tokens.Count), (a, b) => new { smb = a, idx = b })
				.ToDictionary(e => e.smb, e => e.idx);

			/// Строим набор множеств пунктов
			BuildItems(g);

			Actions = new HashSet<Action>[Items.Count, Lookaheads.Count];

			for(var i=0; i<Items.Count;++i)
			{
				foreach (var lookahead in Lookaheads)
					this[i, lookahead.Key] = new HashSet<Action>();

				foreach(var marker in Items[i])
				{
					/// A => alpha * a beta
					if(g[marker.Next] is TerminalSymbol)
					{
						this[i, marker.Next].Add(new ShiftAction()
						{
							TargetItemIndex = Transitions[i][marker.Next]
						});
					}

					/// A => alpha *
					if (String.IsNullOrEmpty(marker.Next) 
						&& marker.Alternative.NonterminalSymbolName != g.StartSymbol)
					{
						this[i, marker.Lookahead].Add(new ReduceAction()
						{
							ReductionAlternative = marker.Alternative
						});
					}
				}

				/// S => ...*, $
				if (Items[i].Any(item=>item.Alternative.NonterminalSymbolName == g.StartSymbol 
					&& String.IsNullOrEmpty(item.Next)
					&& item.Lookahead == Grammar.EOF_TOKEN_NAME))
				{
					this[i, Grammar.EOF_TOKEN_NAME].Add(new AcceptAction());
				}
			}
		}

		private void BuildItems(Grammar g)
		{
			Items = new List<HashSet<Marker>>()
			{
				g.BuildClosure(new HashSet<Marker>(
					(g[g.StartSymbol] as NonterminalSymbol).Alternatives.Select(a=>new Marker(a, 0, Grammar.EOF_TOKEN_NAME))
				))
			};

			Transitions = new List<Dictionary<string, int>>();

			for (var i = 0; i < Items.Count; ++i)
			{
				Transitions.Add(new Dictionary<string, int>());

				foreach (var smb in g.Tokens.Keys.Union(g.Rules.Keys))
				{
					var gotoSet = g.Goto(Items[i], smb);

					if (gotoSet.Count > 0)
					{
						/// Проверяем, не совпадает ли полученное множество 
						/// с каким-либо из имеющихся
						var j = 0;
						for (; j < Items.Count; ++j)
							if (EqualMarkerSets(Items[j], gotoSet))
							{
								break;
							}

						/// Если не нашли совпадение
						if (j == Items.Count)
						{
							Items.Add(gotoSet);
						}

						Transitions[i][smb] = j;
					}
				}
			}
		}

		private bool EqualMarkerSets(HashSet<Marker> a, HashSet<Marker> b)
		{
			if (a.Count != b.Count)
				return false;

			foreach(var elem in a)
			{
				if (!b.Any(e=> e.Position == elem.Position
					&& e.Lookahead == elem.Lookahead
					&& e.Alternative.Equals(elem.Alternative)))
					return false;
			}

			return true;
		}

		public HashSet<Action> this[int i, string lookahead]
		{
			get { return Actions[i, Lookaheads[lookahead]]; }

			private set { Actions[i, Lookaheads[lookahead]] = value; }
		}

		public override List<Message> CheckValidity()
		{
			var errors = new List<Message>();

			for (var itemIdx = 0; itemIdx < Actions.GetLength(0); ++itemIdx)
				for (var lookaheadIdx = 0; lookaheadIdx < Actions.GetLength(1); ++lookaheadIdx)
					if (Actions[itemIdx, lookaheadIdx].Count > 1)
					{
						/// Сразу вытаскиваем токен, по которому возникает неоднозначность
						var lookahead = Lookaheads.FirstOrDefault(l => l.Value == lookaheadIdx).Key;
						/// Формируем строковые представления действий, которые по этому токену можно сделать
						var actions = Actions[itemIdx, lookaheadIdx].Select(a => a is ReduceAction
								? $"{a.ActionName} по ветке {GrammarObject.Userify(((ReduceAction)a).ReductionAlternative)} нетерминала {GrammarObject.Userify(((ReduceAction)a).ReductionAlternative.NonterminalSymbolName)}"
								: $"{a.ActionName}").ToList();

						var messageText = $"Грамматика не является LR(1): для токена {GrammarObject.Userify(lookahead)} и состояния{Environment.NewLine}"
							+ $"\t\t{ToString(itemIdx, lookahead, "\t\t")}{Environment.NewLine}"
							+ $"\tвозможны действия:{Environment.NewLine}" + "\t\t" + String.Join(Environment.NewLine + "\t\t", actions);

						/// Для Shift/Reduce конфликта кидаем предупреждение, а не соо об ошибке
						if (Actions[itemIdx, lookaheadIdx].Count == 2
							&& Actions[itemIdx, lookaheadIdx].Any(a => a is ReduceAction)
							&& Actions[itemIdx, lookaheadIdx].Any(a => a is ShiftAction))
						{
							errors.Add(Message.Warning(
									messageText,
									null,
									"LanD"
								));
						}
						else
						{
							errors.Add(Message.Error(
									messageText,
									null,
									"LanD"
								));
						}
					}

			/// Проверяем состояния на наличие нескольких пунктов перед Any
			for(var i=0; i<Items.Count; ++i)
			{
				if(Items[i].GroupBy(m=>new { m.Alternative, m.Position })
					.Where(g=>g.First().Next == Grammar.ANY_TOKEN_NAME).Count() > 1)
				{
					errors.Add(Message.Error(
						$"Any-конфликт: согласно состоянию{Environment.NewLine}" + $"\t\t{ToString(i, null, "\t\t")}{Environment.NewLine}" 
							+ $"\tпрефикс, оканчивающийся на Any может быть разобран несколькими способами",
						null,
						"LanD"
					));
				}
			}

			return errors;
		}

		public override void ExportToCsv(string filename)
		{
			var output = new StreamWriter(filename);

			var orderedLookaheads = Lookaheads.OrderBy(l => l.Value);
			output.WriteLine("," + String.Join(",", orderedLookaheads.Select(l => l.Key)));

			for(var i=0; i< Items.Count; ++i)
			{
				output.Write($"{i},");

				output.Write(String.Join(",",
					orderedLookaheads.Select(l => this[i, l.Key])
					.Select(alts => alts.Count == 0 ? "" : alts.Count == 1 ? alts.Single().ToString() : String.Join("/", alts))));

				output.WriteLine();
			}

			output.Close();
		}

		public string ToString(int state, string lookahead = null, string padding = "")
		{
			var altPosGroups = Items[state]
				.Where(i=>String.IsNullOrEmpty(lookahead) || i.Lookahead == lookahead)
				.GroupBy(i => new { i.Alternative, i.Position });
			var strings = new List<string>();

			foreach (var group in altPosGroups)
			{
				var userified = GrammarObject.UserifyElementwise(group.Key.Alternative);
				userified.Insert(group.Key.Position, "\u2022");

				var groupString = $"{String.Join("   ", userified)}";
				if (String.IsNullOrEmpty(lookahead))
					groupString += $"    |    {String.Join(", ", group.Select(l => GrammarObject.Userify(l.Lookahead)))}";

				strings.Add(groupString);
            }

			return String.Join($"{Environment.NewLine}{padding}", strings);
		}

		public HashSet<string> GetExpectedTokens(int state)
		{
			return new HashSet<string>(
				Lookaheads.Keys.Where(l => this[state, l].Count > 0)
			);
		}
	}
}
