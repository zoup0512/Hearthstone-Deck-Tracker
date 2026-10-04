using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.ShopAdvisor;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Hearthstone_Deck_Tracker.Utility.Logging;
using BoardCard = HearthMirror.Objects.BoardCard;

namespace Hearthstone_Deck_Tracker.Utility.Battlegrounds
{
	internal class ShopAdvisorRow
	{
		public string Name = "";
		public string Reasons = "";
		public bool Affordable = true;
		public double Score;
	}

	/// <summary>
	/// Coarse "worth buying" hints for the Battlegrounds tavern during the shopping
	/// phase. The shop contents come from the play zone watcher (in Battlegrounds the
	/// opposing play zone is Bob's tavern); scoring is local and heuristic, using the
	/// same Tier7-style pipeline shape (context collection → scoring → overlay) that a
	/// cloud-backed provider could later plug into.
	/// </summary>
	internal static class BattlegroundsShopAdvisor
	{
		// Rough atk+hp baseline per tavern tier; shop minions above it are "above curve".
		private static readonly int[] TierStatBaseline = { 0, 6, 9, 12, 16, 20, 24, 28 };
		private const int MaxRecommendations = 3;

		private static List<BoardCard>? _lastShopCards;
		private static DateTime _lastDiagnosticLog = DateTime.MinValue;

		internal static void OnShopChange(List<BoardCard>? shopCards)
		{
			_lastShopCards = shopCards;
			if(!ShouldAdvise())
				return;
			Refresh();
		}

		internal static void OnShoppingStart()
		{
			if(ShouldAdvise())
				Refresh();
		}

		internal static void Reset()
		{
			SetVisible(false);
		}

		private static bool ShouldAdvise()
		{
			var game = Core.Game;
			return Config.Instance.ShowBattlegroundsShopAdvisor
				&& game.IsBattlegroundsMatch
				&& !game.IsBattlegroundsCombatPhase
				&& game.SetupDone
				&& !game.Spectator
				&& !game.IsInMenu;
		}

		private static void Refresh()
		{
			try
			{
				Compute();
			}
			catch(Exception e)
			{
				Log.Error(e);
			}
		}

		private static void Compute()
		{
			var game = Core.Game;
			var shopCards = _lastShopCards;
			var vm = Core.Overlay.BattlegroundsShopAdvisorVM;
			if(shopCards == null || shopCards.Count == 0)
			{
				SetVisible(false);
				return;
			}

			var gold = game.PlayerEntity?.GetTag(GameTag.RESOURCES) ?? 0;
			var tier = game.Player.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 1;
			var board = game.Player.Board.Where(x => x.IsMinion).ToList();

			var ownedCounts = board.Concat(game.Player.Hand)
				.Where(x => !string.IsNullOrEmpty(x.CardId))
				.GroupBy(x => x.CardId!)
				.ToDictionary(g => g.Key, g => g.Count());
			var boardRaces = board.Select(x => x.Card?.RaceEnum)
				.Where(r => r is Race race && race != Race.ALL && race != Race.INVALID)
				.ToList();

			// Tier7 comps guides already flag cards worth picking for the current lobby;
			// factor that cloud signal in when the marker feature populated it.
			var recommendedIds = Core.Overlay.BattlegroundsMinionPinningViewModel.GetRecommendedCardIds();

			var rows = new List<ShopAdvisorRow>();
			foreach(var slot in shopCards.Select((card, index) => (card, index)))
			{
				var cardId = slot.card.CardId;
				if(string.IsNullOrEmpty(cardId))
					continue;
				var card = Database.GetCardFromId(cardId);
				if(card == null || !card.IsKnownCard || !card.IsBaconMinion)
					continue;

				var entity = slot.card.EntityId is int entityId && game.Entities.TryGetValue(entityId, out var e) ? e : null;
				var attack = entity?.GetTag(GameTag.ATK) ?? card.Attack;
				var health = entity?.GetTag(GameTag.HEALTH) ?? card.Health;
				var cost = entity is { } e2 && e2.HasTag(GameTag.COST) ? e2.GetTag(GameTag.COST) : card.Cost;
				var cardTier = card.TechLevel;
				var race = card.RaceEnum;

				double score = 0;
				var reasons = new List<string>();

				// Triple potential: the strongest, most actionable signal.
				var owned = ownedCounts.TryGetValue(cardId!, out var oc) ? oc : 0;
				if(owned >= 2)
				{
					score += 100;
					reasons.Add(Loc("ShopAdvisor_Triple", "Triple!"));
				}
				else if(owned == 1)
				{
					score += 15;
				}

				// Tribal synergy with the current board.
				if(race is Race r && r != Race.ALL && r != Race.INVALID)
				{
					var matches = boardRaces.Count(x => x == race);
					if(matches > 0)
					{
						score += 8 * Math.Min(matches, 3);
						reasons.Add(string.Format(Loc("ShopAdvisor_Synergy", "{0} matching tribes"), matches));
					}
				}

				// Stat line quality relative to the tavern tier's typical stats.
				var stats = attack + health;
				var baseline = cardTier >= 0 && cardTier < TierStatBaseline.Length ? TierStatBaseline[cardTier] : 12;
				if(baseline > 0 && stats > baseline * 1.2)
				{
					score += Math.Min(20, 12 * (stats / (double)baseline - 1));
					reasons.Add(Loc("ShopAdvisor_AboveCurve", "above curve"));
				}

				// Tier curve: minions that just unlocked or are at the current tier
				// are the usual tempo picks.
				if(cardTier == tier + 1)
				{
					score += 10;
					reasons.Add(string.Format(Loc("ShopAdvisor_NextTier", "tier {0} unlocked"), cardTier));
				}
				else if(cardTier == tier)
				{
					score += 5;
				}

				if(recommendedIds.Contains(cardId))
				{
					score += 25;
					reasons.Add(Loc("ShopAdvisor_Guide", "guide pick"));
				}

				var affordable = cost <= gold;
				if(!affordable)
				{
					score -= 60;
					reasons.Insert(0, Loc("ShopAdvisor_CantAfford", "not enough gold"));
				}

				rows.Add(new ShopAdvisorRow
				{
					Name = card.LocalizedName ?? cardId,
					Reasons = string.Join(" · ", reasons),
					Affordable = affordable,
					Score = score,
				});
			}

			var top = rows.OrderByDescending(r => r.Score).Take(MaxRecommendations).ToList();
			if((DateTime.UtcNow - _lastDiagnosticLog).TotalMilliseconds > 2000)
			{
				_lastDiagnosticLog = DateTime.UtcNow;
				Log.Info($"[ShopAdvisor] shopCards={shopCards.Count} scoredRows={rows.Count} visible={top.Count > 0}");
			}
			SetVisible(top.Count > 0, top);
		}

		private static void SetVisible(bool visible, List<ShopAdvisorRow>? rows = null)
		{
			var vm = Core.Overlay.BattlegroundsShopAdvisorVM;
			vm.Update(rows ?? new List<ShopAdvisorRow>(), visible);
		}

		// The Strings resx files are copied from the HDT-Localization repo at build time,
		// so a fresh clone may not carry these keys; fall back to English.
		private static string Loc(string key, string fallback)
		{
			var localized = LocUtil.Get(key);
			return string.IsNullOrEmpty(localized) ? fallback : localized;
		}
	}
}
