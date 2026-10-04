using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;
using Hearthstone_Deck_Tracker.Utility.MVVM;

namespace Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.ShopAdvisor
{
	public class ShopAdvisorRowViewModel
	{
		public string Name { get; set; } = "";
		public string Reasons { get; set; } = "";
		public bool Affordable { get; set; } = true;
	}

	public class BattlegroundsShopAdvisorViewModel : ViewModel
	{
		public double Scaling { get => GetProp(1.0); set => SetProp(value); }

		public List<ShopAdvisorRowViewModel> Recommendations
		{
			get => GetProp(new List<ShopAdvisorRowViewModel>()) ?? new List<ShopAdvisorRowViewModel>();
			set
			{
				SetProp(value);
				OnPropertyChanged(nameof(IsVisible));
			}
		}

		public bool IsEnabled
		{
			get => GetProp(false);
			set
			{
				SetProp(value);
				OnPropertyChanged(nameof(IsVisible));
			}
		}

		public Visibility IsVisible => IsEnabled && Recommendations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		internal void Update(List<ShopAdvisorRow> rows, bool enabled)
		{
			Recommendations = rows.Select(r => new ShopAdvisorRowViewModel
			{
				Name = r.Name,
				Reasons = r.Reasons,
				Affordable = r.Affordable,
			}).ToList();
			IsEnabled = enabled;
		}
	}
}
