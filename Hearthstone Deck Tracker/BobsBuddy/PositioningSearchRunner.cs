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
	/// How dangerous the upcoming combat is for the current arrangement, expressed in
	/// baseline win/tie/loss rates plus how much uniform stats the player's board would
	/// need to reach a coin-flip.
	/// </summary>
	internal class ThreatAssessment
	{
		public double WinRate;
		public double TieRate;
		public double LossRate;
		public double AvgDamageTakenOnLoss;
		public int StatsNeededForCoinFlip;   // uniform +X/+X, -1 = unknown, 0 = already favored
		public double StatsNeededWinRate;
	}

	/// <summary>
	/// During the Battlegrounds shopping phase, searches for the board arrangement that
	/// maximizes the simulated combat result against the upcoming opponent, and assesses
	/// how threatening the next combat is. Both run on the same snapshot: the player's
	/// board is still editable during shopping, which is what makes this the right moment.
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

		// Threat sweep: uniform +X/+X applied to the whole board, in steps, until the
		// simulated result reaches a coin flip.
		private const int ThreatEvalIterations = 250;
		private const int ThreatEvalTimeBudgetMs = 400;
		private const int ThreatBuffStep = 2;
		private const int ThreatMaxBuff = 12;
		private const double FavoredThreshold = 0.55;

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
				await RunSearch(input!, ct);

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
					await RunSearch(input!, ct);
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

		private static async Task RunSearch(Input input, CancellationToken ct)
		{
			var (hint, threat) = await SearchAsync(input, ct);
			Core.Overlay.BobsBuddyDisplay.ShowThreatAssessment(threat != null ? FormatThreat(threat) : null);
			if(hint != null)
				Core.Overlay.BobsBuddyDisplay.ShowPositioningHint(FormatHint(hint));
			else
				Core.Overlay.BobsBuddyDisplay.HidePositioningLine();
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

		private static async Task<(PositioningHint? Hint, ThreatAssessment? Threat)> SearchAsync(Input input, CancellationToken ct)
		{
			var deadline = Stopwatch.StartNew();
			var side = input.Player.Side;
			var count = side.Count;
			// References in the current board order. Side is mutated in place between runs;
			// the simulator clones per iteration, so the minions themselves are never touched.
			var original = side.ToList();
			var originalOrder = Enumerable.Range(0, count).ToList();

			// Baseline run doubles as the input for the threat assessment.
			var baselineOutput = await Simulate(input, ConfirmIterations, ConfirmTimeBudgetMs);
			if(baselineOutput == null)
				return (null, null);
			var threat = await AssessThreatAsync(input, baselineOutput, original, ct);

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

			if(bestOrder.SequenceEqual(originalOrder))
				return (null, threat);

			// Re-confirm baseline and best with larger samples so simulation noise cannot
			// surface a "better" arrangement that is actually equivalent or worse.
			Apply(side, bestOrder);
			var bestOutput = await Simulate(input, ConfirmIterations, ConfirmTimeBudgetMs);
			Apply(side, originalOrder);
			var currentOutput = baselineOutput;
			if(bestOutput == null)
				return (null, threat);
			if(Score(bestOutput) <= Score(currentOutput) + ConfirmMargin)
				return (null, threat);

			return (new PositioningHint
			{
				Names = bestOrder.Select(i => GetMinionName(original[i])).ToList(),
				BestWinRate = bestOutput.winRate,
				BestTieRate = bestOutput.tieRate,
				CurrentWinRate = currentOutput.winRate,
				CurrentTieRate = currentOutput.tieRate,
			}, threat);
		}

		/// <summary>
		/// Sweeps uniform +X/+X buffs over the player's board until the simulated combat
		/// reaches a coin flip, reporting the smallest X that gets there. The minions belong
		/// to a throwaway snapshot, so mutating their base stats is safe as long as the
		/// original values are restored afterwards.
		/// </summary>
		private static async Task<ThreatAssessment?> AssessThreatAsync(
			Input input, Output baseline, List<BobsBuddyMinion> minions, CancellationToken ct)
		{
			var threat = new ThreatAssessment
			{
				WinRate = baseline.winRate,
				TieRate = baseline.tieRate,
				LossRate = baseline.lossRate,
				AvgDamageTakenOnLoss = baseline.damageResults?.Where(x => x < 0).DefaultIfEmpty(0).Average() ?? 0,
			};
			var originalAttack = minions.Select(m => m.baseAttack).ToArray();
			var originalHealth = minions.Select(m => m.baseHealth).ToArray();
			try
			{
				if(Score(baseline) >= FavoredThreshold)
				{
					threat.StatsNeededForCoinFlip = 0;
					return threat;
				}

				for(var buff = ThreatBuffStep; buff <= ThreatMaxBuff; buff += ThreatBuffStep)
				{
					ct.ThrowIfCancellationRequested();
					for(var i = 0; i < minions.Count; i++)
					{
						minions[i].baseAttack = originalAttack[i] + buff;
						minions[i].baseHealth = originalHealth[i] + buff;
					}
					var output = await Simulate(input, ThreatEvalIterations, ThreatEvalTimeBudgetMs);
					if(output == null)
						break;
					if(Score(output) >= 0.5)
					{
						threat.StatsNeededForCoinFlip = buff;
						threat.StatsNeededWinRate = output.winRate;
						break;
					}
				}
				return threat;
			}
			finally
			{
				// The sweep shares its input with the positioning search that runs next;
				// left-over hypothetical buffs would corrupt every subsequent evaluation.
				// Cancellation can interrupt mid-sweep, hence finally.
				for(var i = 0; i < minions.Count; i++)
				{
					minions[i].baseAttack = originalAttack[i];
					minions[i].baseHealth = originalHealth[i];
				}
			}
		}

		private static string FormatThreat(ThreatAssessment threat)
		{
			var line = string.Format(
				// Percent() already appends "%", so the templates must not repeat it.
				Loc("BobsBuddyThreat_Line", "Threat: W {0} / T {1} / L {2}"),
				Percent(threat.WinRate), Percent(threat.TieRate), Percent(threat.LossRate));
			if(threat.AvgDamageTakenOnLoss < -0.5)
				line += " ｜ " + string.Format(
					Loc("BobsBuddyThreat_AvgDamageTaken", "avg {0} dmg taken on loss"),
					(-threat.AvgDamageTakenOnLoss).ToString("0.#"));
			if(threat.StatsNeededForCoinFlip == 0)
				line += " ｜ " + Loc("BobsBuddyThreat_Favored", "favored");
			else if(threat.StatsNeededForCoinFlip > 0)
				line += " ｜ " + string.Format(
					Loc("BobsBuddyThreat_NeedsBuff", "+{0}/+{1} → W {2}"),
					threat.StatsNeededForCoinFlip, threat.StatsNeededForCoinFlip, Percent(threat.StatsNeededWinRate));
			else
				line += " ｜ " + string.Format(
					Loc("BobsBuddyThreat_Outmatched", "+{0}/+{0} still unlikely to win"), ThreatMaxBuff);
			return line;
		}

		private static string FormatHint(PositioningHint hint)
		{
			var order = string.Join(" → ", hint.Names);
			return string.Format(
				Loc("BobsBuddyPositioningHint_Order", "Suggested placement: {0}"), order) + "\n" +
				string.Format(
					Loc("BobsBuddyPositioningHint_Comparison", "Win {0} / Tie {1} (current: Win {2} / Tie {3})"),
					Percent(hint.BestWinRate), Percent(hint.BestTieRate),
					Percent(hint.CurrentWinRate), Percent(hint.CurrentTieRate));
		}

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
