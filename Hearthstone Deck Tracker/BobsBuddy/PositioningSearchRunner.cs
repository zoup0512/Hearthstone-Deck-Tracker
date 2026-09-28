using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BobsBuddy.Simulation;
using BobsBuddyMinion = BobsBuddy.Minion;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Utility;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Hearthstone_Deck_Tracker.Utility.Logging;

namespace Hearthstone_Deck_Tracker.BobsBuddy
{
	internal class PositioningHint
	{
		public List<string> Names = new();
		public double BestWinRate;
		public double BestTieRate;
		public double CurrentWinRate;
		public double CurrentTieRate;
	}

	/// <summary>
	/// During the Battlegrounds shopping phase, searches for the board arrangement that
	/// maximizes the simulated combat result against the upcoming opponent, and surfaces
	/// a hint on the BobsBuddy panel. The player's board is still editable during shopping,
	/// which is what makes this the right moment for a placement recommendation.
	/// </summary>
	internal static class PositioningSearchRunner
	{
		private const int InitialDelayMs = 5000;
		private const int RecheckDelayMs = 4000;
		private const int SnapshotRetryDelayMs = 3000;
		private const int MaxSnapshotRetries = 4;
		private const int SearchDeadlineMs = 25_000;

		// Per-arrangement evaluation budget. SimulationRunner stops at whichever comes
		// first: iteration count or time cap, so these bound each candidate's cost.
		private const int EvalIterations = 120;
		private const int EvalTimeBudgetMs = 250;
		private const int ConfirmIterations = 1200;
		private const int ConfirmTimeBudgetMs = 900;

		// 4! = 24 permutations are cheap enough to evaluate exhaustively. Larger boards
		// (up to 7! = 5040) fall back to pairwise-swap hill climbing.
		private const int MaxExhaustiveMinions = 4;
		private const int MaxHillClimbRounds = 3;

		// With ~1200 confirmations the stderr of a win rate is ~1.3%, so a difference
		// below this margin cannot be distinguished from simulation noise.
		private const double ConfirmMargin = 0.035;

		private static readonly object Lock = new object();
		private static CancellationTokenSource? _cts;

		internal static void OnShoppingStart()
		{
			if(!Config.Instance.RunBobsBuddy || !Config.Instance.ShowBobsBuddyPositioningHint)
				return;
			if(Core.Game.IsBattlegroundsDuosMatch)
				return;
			CancellationToken token;
			lock(Lock)
			{
				CancelInternal();
				_cts = new CancellationTokenSource();
				token = _cts.Token;
			}
			RunLoopAsync(token).Forget();
		}

		internal static void OnCombatStart()
		{
			lock(Lock) CancelInternal();
		}

		internal static void OnGameOver()
		{
			lock(Lock) CancelInternal();
		}

		private static void CancelInternal()
		{
			_cts?.Cancel();
			_cts = null;
			Core.Overlay.BobsBuddyDisplay.HidePositioningHint();
		}

		private static async Task RunLoopAsync(CancellationToken ct)
		{
			try
			{
				// The upcoming opponent's board is not revealed the instant shopping starts;
				// give the client a moment before snapshotting.
				await Task.Delay(InitialDelayMs, ct);

				Input? input = null;
				for(var attempt = 0; attempt < MaxSnapshotRetries; attempt++)
				{
					input = BobsBuddyInvoker.SnapshotForPositioningSearch();
					if(IsValid(input))
						break;
					input = null;
					await Task.Delay(SnapshotRetryDelayMs, ct);
				}
				if(!IsValid(input))
					return;

				var lastFingerprint = BoardFingerprint(input!);
				var hint = await SearchAsync(input!, ct);
				if(hint != null)
					DisplayHint(hint);

				// The player keeps buying, selling and reordering during shopping; re-run
				// whenever the relevant state actually changed.
				while(true)
				{
					await Task.Delay(RecheckDelayMs, ct);
					input = BobsBuddyInvoker.SnapshotForPositioningSearch();
					if(!IsValid(input))
						return;
					var fingerprint = BoardFingerprint(input!);
					if(fingerprint == lastFingerprint)
						continue;
					lastFingerprint = fingerprint;
					hint = await SearchAsync(input!, ct);
					if(hint != null)
						DisplayHint(hint);
				}
			}
			catch(OperationCanceledException)
			{
			}
			catch(Exception e)
			{
				Log.Error(e);
			}
		}

		private static bool IsValid(Input? input) =>
			input != null && input.Player.Side.Count >= 2 && input.Opponent.Side.Count >= 1;

		private static string BoardFingerprint(Input input) =>
			string.Join("|", input.Player.Side.Select(MinionKey)) + "##" +
			string.Join("|", input.Opponent.Side.Select(MinionKey));

		private static double Score(Output output) => output.winRate + 0.5 * output.tieRate;

		private static async Task<Output?> Simulate(Input input, int iterations, int timeBudgetMs)
		{
			try
			{
				return await new SimulationRunner().SimulateMultiThreaded(
					input, iterations, BobsBuddyInvoker.ThreadCount, timeBudgetMs);
			}
			catch(Exception e)
			{
				Log.Error(e);
				return null;
			}
		}

		private static async Task<PositioningHint?> SearchAsync(Input input, CancellationToken ct)
		{
			var deadline = Stopwatch.StartNew();
			var side = input.Player.Side;
			var count = side.Count;
			// References in the current board order. Side is mutated in place between runs;
			// the simulator clones per iteration, so the minions themselves are never touched.
			var original = side.ToList();
			var originalOrder = Enumerable.Range(0, count).ToList();

			async Task<double> Evaluate(IReadOnlyList<int> order)
			{
				Apply(side, order);
				var output = await Simulate(input, EvalIterations, EvalTimeBudgetMs);
				return output == null ? double.MinValue : Score(output);
			}

			double bestScore;
			List<int> bestOrder;

			if(count <= MaxExhaustiveMinions)
			{
				bestScore = double.MinValue;
				bestOrder = original.Select((_, i) => i).ToList();
				var minionKeys = original.Select(MinionKey).ToList();
				foreach(var perm in DistinctPermutations(minionKeys))
				{
					ct.ThrowIfCancellationRequested();
					if(deadline.ElapsedMilliseconds > SearchDeadlineMs)
						break;
					var score = await Evaluate(perm);
					if(score > bestScore)
					{
						bestScore = score;
						bestOrder = perm.ToList();
					}
				}
			}
			else
			{
				bestOrder = Enumerable.Range(0, count).ToList();
				bestScore = await Evaluate(bestOrder);
				for(var round = 0; round < MaxHillClimbRounds; round++)
				{
					var improved = false;
					for(var i = 0; i < count - 1; i++)
					{
						for(var j = i + 1; j < count; j++)
						{
							ct.ThrowIfCancellationRequested();
							if(deadline.ElapsedMilliseconds > SearchDeadlineMs)
								break;
							(bestOrder[i], bestOrder[j]) = (bestOrder[j], bestOrder[i]);
							var score = await Evaluate(bestOrder);
							if(score > bestScore + 1e-9)
							{
								bestScore = score;
								improved = true;
							}
							else
								(bestOrder[i], bestOrder[j]) = (bestOrder[j], bestOrder[i]);
						}
						if(deadline.ElapsedMilliseconds > SearchDeadlineMs)
							break;
					}
					if(!improved || deadline.ElapsedMilliseconds > SearchDeadlineMs)
						break;
				}
			}

			if(bestOrder.SequenceEqual(Enumerable.Range(0, count)))
				return null; // the current arrangement is already the best known

			// Re-confirm baseline and best with larger samples so simulation noise cannot
			// surface a "better" arrangement that is actually equivalent or worse.
			Apply(side, bestOrder);
			var bestOutput = await Simulate(input, ConfirmIterations, ConfirmTimeBudgetMs);
			Apply(side, originalOrder);
			var currentOutput = await Simulate(input, ConfirmIterations, ConfirmTimeBudgetMs);
			if(bestOutput == null || currentOutput == null)
				return null;
			if(Score(bestOutput) <= Score(currentOutput) + ConfirmMargin)
				return null;

			return new PositioningHint
			{
				Names = bestOrder.Select(i => GetMinionName(original[i])).ToList(),
				BestWinRate = bestOutput.winRate,
				BestTieRate = bestOutput.tieRate,
				CurrentWinRate = currentOutput.winRate,
				CurrentTieRate = currentOutput.tieRate,
			};
		}

		/// <summary>
		/// Rearranges side so that side[k] becomes the minion currently at order[k].
		/// Works on the list of references only; the minions themselves are untouched.
		/// </summary>
		private static void Apply(List<BobsBuddyMinion> side, IReadOnlyList<int> order)
		{
			var temp = new List<BobsBuddyMinion>(order.Count);
			foreach(var index in order)
				temp.Add(side[index]);
			for(var k = 0; k < order.Count; k++)
				side[k] = temp[k];
		}

		private static string GetMinionName(BobsBuddyMinion minion) =>
			Database.GetCardFromId(minion.CardID)?.LocalizedName ?? minion.CardID ?? "?";

		private static string MinionKey(BobsBuddyMinion minion) => minion.CardID + "#" + minion;

		/// <summary>
		/// All distinct permutations of 0..keys.Count-1, skipping arrangements that are
		/// equivalent because they only swap minions with identical keys.
		/// </summary>
		private static IEnumerable<List<int>> DistinctPermutations(IReadOnlyList<string> keys)
		{
			var count = keys.Count;
			var current = new int[count];
			var used = new bool[count];

			IEnumerable<List<int>> Recurse(int depth)
			{
				if(depth == count)
				{
					yield return current.ToList();
					yield break;
				}
				var seenAtDepth = new HashSet<string>();
				for(var i = 0; i < count; i++)
				{
					if(used[i])
						continue;
					if(!seenAtDepth.Add(keys[i]))
						continue;
					used[i] = true;
					current[depth] = i;
					foreach(var rest in Recurse(depth + 1))
						yield return rest;
					used[i] = false;
				}
			}

			return Recurse(0);
		}

		private static void DisplayHint(PositioningHint hint)
		{
			var order = string.Join(" → ", hint.Names);
			var text = string.Format(Loc("BobsBuddyPositioningHint_Order", "Suggested placement: {0}"), order) + "\n" +
				string.Format(Loc("BobsBuddyPositioningHint_Comparison", "Win {0} / Tie {1} (current: Win {2} / Tie {3})"),
					Percent(hint.BestWinRate), Percent(hint.BestTieRate),
					Percent(hint.CurrentWinRate), Percent(hint.CurrentTieRate));
			Core.Overlay.BobsBuddyDisplay.ShowPositioningHint(text);
		}

		// The Strings resx files are copied from the HDT-Localization repo at build time,
		// so a fresh clone may not carry these keys; fall back to English.
		private static string Loc(string key, string fallback)
		{
			var localized = LocUtil.Get(key);
			return string.IsNullOrEmpty(localized) ? fallback : localized;
		}

		private static string Percent(double rate) => (rate * 100).ToString("0.#") + "%";
	}
}
