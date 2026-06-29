// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace _100_Ticks_Trade_Indicator_ICT
{
	/// <summary>
	/// 100 Ticks Trade Indicator for MGC (Micro Gold Futures)
	/// ICT-based trading indicator with entry/exit targets and risk management
	/// Features: 4 trades/day, 100+ ticks profit target, $50 max risk, $100 daily loss limit
	/// </summary>
	public class _100_Ticks_Trade_Indicator_ICT : Indicator
	{
		#region Enums
		private enum TradingState
		{
			Idle,               // Not in a trade, searching for entry
			SearchingEntry,     // Monitoring for entry signal
			InTrade,            // Position is open
			ExitPending         // Exit signal triggered, waiting for fill
		}
		#endregion

		#region Constants
		private const double TICKS_PER_TRADE_TARGET = 100;
		private const double MAX_RISK_PER_TRADE_TICKS = 50;
		private const double DAILY_LOSS_LIMIT = 100;
		private const int MAX_TRADES_PER_DAY = 4;
		private const int FIXED_LOT_SIZE = 1;
		private const double MGC_TICK_VALUE = 1.00; // $1 per tick
		private const string SYMBOL_NAME = "MGC";
		private const string EXCHANGE_NAME = "CME";
		#endregion

		#region Fields - Risk Management
		private int _tradesExecutedToday = 0;
		private double _dailyRealizedPnL = 0;
		private double _sessionStartBalance = 0;
		private DateTime _sessionDate = DateTime.MinValue;

		// Trade tracking
		private List<TradeRecord> _executedTrades = new List<TradeRecord>();
		private TradeRecord _currentTrade = null;
		#endregion

		#region Fields - Platform Integration
		private Account _account = null;
		private Symbol _symbol = null;
		private Position _currentPosition = null;
		private TradingState _state = TradingState.Idle;
		private Order _pendingOrder = null;
		#endregion

		#region Fields - Signal Detection
		private double _lastBid = 0;
		private double _lastAsk = 0;
		private double _entryPrice = 0;
		private double _profitTarget = 0;
		private double _stopLoss = 0;
		#endregion

		/// <summary>
		/// Trade record for tracking daily trades
		/// </summary>
		private class TradeRecord
		{
			public DateTime EntryTime { get; set; }
			public double EntryPrice { get; set; }
			public double ExitPrice { get; set; }
			public double ProfitTicks { get; set; }
			public double RealizedPnL { get; set; }
			public bool IsClosed { get; set; }
		}

		/// <summary>
		/// Indicator's constructor
		/// </summary>
		public _100_Ticks_Trade_Indicator_ICT()
			: base()
		{
			Name = "100 Ticks Trade Indicator ICT";
			Description = "MGC trading indicator: 4 trades/day, 100+ ticks profit, $50 risk, $100 daily limit";

			// Main chart display line (for entry levels)
			AddLineSeries("EntryTarget", Color.LimeGreen, 2);
			AddLineSeries("ProfitTarget", Color.Blue, 2);
			AddLineSeries("StopLoss", Color.Red, 2);

			SeparateWindow = false;
		}

		#region Input Parameters
		[InputParameter("Account Name Filter", 0)]
		public string AccountNameFilter { get; set; } = "Lucid";

		[InputParameter("Enable Trading", 1)]
		public bool EnableTrading { get; set; } = true;

		[InputParameter("Target Ticks Per Trade", 2, 50, 50, 200, 10)]
		public double TargetTicksPerTrade { get; set; } = TICKS_PER_TRADE_TARGET;

		[InputParameter("Max Risk Ticks Per Trade", 3, 25, 10, 100, 5)]
		public double MaxRiskTicksPerTrade { get; set; } = MAX_RISK_PER_TRADE_TICKS;

		[InputParameter("Daily Loss Limit ($)", 4, 100, 50, 500, 50)]
		public double DailyLossLimitDollars { get; set; } = DAILY_LOSS_LIMIT;

		[InputParameter("Max Trades Per Day", 5, 4, 1, 10, 1)]
		public int MaxTradesPerDay { get; set; } = MAX_TRADES_PER_DAY;

		[InputParameter("Lot Size", 6, 1, 1, 5, 1)]
		public int LotSize { get; set; } = FIXED_LOT_SIZE;
		#endregion

		/// <summary>
		/// Initialization - called after indicator creation or parameter reset
		/// </summary>
		protected override void OnInit()
		{
			try
			{
				// Resolve trading account
				_account = Core.Instance.Accounts
					.FirstOrDefault(a => a.Name.Contains(AccountNameFilter));

				if (_account == null)
				{
					DebugLog($"[ERROR] Account not found with filter: {AccountNameFilter}");
					return;
				}

				// Resolve symbol
				_symbol = Core.Instance.Symbols
					.FirstOrDefault(s => s.Name == SYMBOL_NAME && s.Exchange != null && s.Exchange.ExchangeName == EXCHANGE_NAME);

				if (_symbol == null)
				{
					DebugLog($"[ERROR] Symbol {SYMBOL_NAME} not found on {EXCHANGE_NAME}");
					return;
				}

				// Initialize session tracking
				_sessionDate = DateTime.Today;
				_sessionStartBalance = _account.Balance;
				_tradesExecutedToday = 0;
				_dailyRealizedPnL = 0;
				_executedTrades.Clear();
				_state = TradingState.Idle;

				DebugLog($"[INIT] Indicator initialized. Account={_account.Name}, Symbol={_symbol.Name}, " +
					$"Balance={_account.Balance:C2}, TargetTicks={TargetTicksPerTrade}, MaxRisk={MaxRiskTicksPerTrade}");
			}
			catch (Exception ex)
			{
				DebugLog($"[ERROR] Initialization failed: {ex.Message}");
			}
		}

		private void DebugLog(string message)
		{
			// Output debug info to console/platform logging if needed
			System.Diagnostics.Debug.WriteLine($"[MGC100T] {message}");
		}

		/// <summary>
		/// Main calculation entry point
		/// </summary>
		protected override void OnUpdate(UpdateArgs args)
		{
			try
			{
				if (_account == null || _symbol == null)
					return;

				// Reset session if new day started
				CheckSessionReset();

				// Update current position from platform
				UpdatePositionState();

				// Update quote information
				_lastBid = this.Bid();
				_lastAsk = this.Ask();

				// State machine execution
				ProcessTradingState();

				// Update display information
				UpdateDisplayInfo();
			}
			catch (Exception ex)
			{
				DebugLog($"[ERROR] Update processing failed: {ex.Message}");
			}
		}

		private void CheckSessionReset()
		{
			if (DateTime.Today != _sessionDate)
			{
				_sessionDate = DateTime.Today;
				_sessionStartBalance = _account.Balance;
				_tradesExecutedToday = 0;
				_dailyRealizedPnL = 0;
				_executedTrades.Clear();
				_state = TradingState.Idle;

				DebugLog($"[SESSION] New trading day. Balance reset to {_account.Balance:C2}");
			}
		}

		private void UpdatePositionState()
		{
			// Find current position for our symbol
			_currentPosition = Core.Instance.Positions
				.FirstOrDefault(p => p.Symbol == _symbol && p.Account == _account);

			// Update account-level P&L (use GrossPnl for positions, calculate session loss separately)
			if (_currentPosition != null)
			{
				_dailyRealizedPnL = _sessionStartBalance - _account.Balance + CurrentPositionPnL;
			}
			else
			{
				_dailyRealizedPnL = _sessionStartBalance - _account.Balance;
			}
		}

		private void ProcessTradingState()
		{
			// Check risk limits before doing anything
			if (!IsRiskLimitAvailable())
			{
				_state = TradingState.Idle;
				return;
			}

			switch (_state)
			{
				case TradingState.Idle:
					ProcessIdleState();
					break;

				case TradingState.SearchingEntry:
					ProcessSearchingEntryState();
					break;

				case TradingState.InTrade:
					ProcessInTradeState();
					break;

				case TradingState.ExitPending:
					ProcessExitPendingState();
					break;
			}
		}

		private void ProcessIdleState()
		{
			// Can we take another trade today?
			if (_tradesExecutedToday >= MaxTradesPerDay)
				return;

			// Look for entry signal (simple order block concept)
			if (DetectEntrySignal())
			{
				_state = TradingState.SearchingEntry;
				DebugLog($"[SIGNAL] Entry signal detected. Price={_lastAsk:F1}");
			}
		}

		private void ProcessSearchingEntryState()
		{
			// Wait for optimal entry confirmation (next bar, specific level, etc.)
			// For now, simple confirmation: hold for one tick
			if (DetectConfirmedEntry())
			{
				SubmitEntryOrder();
				_state = TradingState.InTrade;
			}
		}

		private void ProcessInTradeState()
		{
			if (_currentPosition == null || _currentPosition.Quantity == 0)
			{
				_state = TradingState.Idle;
				return;
			}

			// Check exit conditions
			double currentPrice = _currentPosition.Quantity > 0 ? _lastBid : _lastAsk;

			// Check profit target
			if (HasHitProfitTarget(currentPrice))
			{
				_state = TradingState.ExitPending;
				SubmitExitOrder("TP_HIT");
			}
			// Check stop loss
			else if (HasHitStopLoss(currentPrice))
			{
				_state = TradingState.ExitPending;
				SubmitExitOrder("SL_HIT");
			}
		}

		private void ProcessExitPendingState()
		{
			// Wait for exit order to fill
			if (_currentPosition == null || _currentPosition.Quantity == 0)
			{
				// Position closed
				_state = TradingState.Idle;
				_tradesExecutedToday++;

				if (_currentTrade != null)
				{
					_currentTrade.IsClosed = true;
					_executedTrades.Add(_currentTrade);
				}

				DebugLog($"[EXIT] Position closed. Trades today: {_tradesExecutedToday}/{MaxTradesPerDay}, Daily P&L: {_dailyRealizedPnL:C2}");
			}
		}

		#region Signal Detection
		private bool DetectEntrySignal()
		{
			// Simple order block detection: look for price rejection patterns
			// This is a simplified version - in production, use more sophisticated ICT analysis

			if (Count < 3)
				return false;

			// Check for bullish reversal setup
			double previousClose = Close(2);
			double previousLow = Low(2);
			double currentBid = _lastBid;

			// Simple: previous bar was lower, current price bounced
			if (currentBid > previousClose && previousClose > previousLow)
			{
				_entryPrice = currentBid;
				_profitTarget = _entryPrice + (TargetTicksPerTrade * _symbol.TickSize);
				_stopLoss = _entryPrice - (MaxRiskTicksPerTrade * _symbol.TickSize);
				return true;
			}

			return false;
		}

		private bool DetectConfirmedEntry()
		{
			// Simplified confirmation: price remains above entry level
			return _lastAsk >= _entryPrice;
		}

		private bool HasHitProfitTarget(double currentPrice)
		{
			return currentPrice >= _profitTarget;
		}

		private bool HasHitStopLoss(double currentPrice)
		{
			return currentPrice <= _stopLoss;
		}
		#endregion

		#region Order Management
		private void SubmitEntryOrder()
		{
			if (!EnableTrading || _account == null || _symbol == null)
				return;

			try
			{
				var parameters = new PlaceOrderRequestParameters
				{
					Symbol = _symbol,
					Account = _account,
					Side = Side.Buy,
					OrderTypeId = OrderType.Market,
					Quantity = LotSize,
					Comment = $"ICT_Entry_Trade{_tradesExecutedToday + 1}_TP{TargetTicksPerTrade}"
				};

				var result = Core.Instance.PlaceOrder(parameters);

				_currentTrade = new TradeRecord
				{
					EntryTime = DateTime.UtcNow,
					EntryPrice = _lastAsk
				};

				DebugLog($"[ORDER] Entry order submitted. Symbol={_symbol.Name}, Qty={LotSize}, EntryPrice={_entryPrice:F1}, TP={_profitTarget:F1}, SL={_stopLoss:F1}");
			}
			catch (Exception ex)
			{
				DebugLog($"[ERROR] Failed to submit entry order: {ex.Message}");
			}
		}

		private void SubmitExitOrder(string reason)
		{
			if (!EnableTrading || _currentPosition == null || _currentPosition.Quantity == 0)
				return;

			try
			{
				var parameters = new PlaceOrderRequestParameters
				{
					Symbol = _symbol,
					Account = _account,
					Side = _currentPosition.Quantity > 0 ? Side.Sell : Side.Buy,
					OrderTypeId = OrderType.Market,
					Quantity = Math.Abs(_currentPosition.Quantity),
					Comment = $"ICT_Exit_{reason}"
				};

				Core.Instance.PlaceOrder(parameters);

				DebugLog($"[ORDER] Exit order submitted. Reason={reason}, Qty={_currentPosition.Quantity}");
			}
			catch (Exception ex)
			{
				DebugLog($"[ERROR] Failed to submit exit order: {ex.Message}");
			}
		}
		#endregion

		#region Risk Management
		private bool IsRiskLimitAvailable()
		{
			// Check daily loss limit
			if (_dailyRealizedPnL <= -DailyLossLimitDollars)
			{
				DebugLog($"[RISK] Daily loss limit reached: {_dailyRealizedPnL:C2} / {-DailyLossLimitDollars:C2}");
				return false;
			}

			// Check trade count
			if (_tradesExecutedToday >= MaxTradesPerDay)
			{
				DebugLog($"[RISK] Max trades per day reached: {_tradesExecutedToday}/{MaxTradesPerDay}");
				return false;
			}

			return true;
		}
		#endregion

		#region Properties for Display
		/// <summary>
		/// Gets current account balance for display
		/// </summary>
		public double CurrentBalance => _account?.Balance ?? 0;

		/// <summary>
		/// Gets current account equity
		/// </summary>
		public double CurrentEquity => CurrentBalance + CurrentPositionPnL;

		/// <summary>
		/// Gets daily realized P&L
		/// </summary>
		public double DailyRealizedPnL => _dailyRealizedPnL;

		/// <summary>
		/// Gets number of trades executed today
		/// </summary>
		public int TradesExecutedToday => _tradesExecutedToday;

		/// <summary>
		/// Gets daily loss limit remaining
		/// </summary>
		public double RemainingDailyLossLimit => Math.Max(0, -DailyLossLimitDollars - _dailyRealizedPnL);

		/// <summary>
		/// Gets current trading state
		/// </summary>
		public string TradingStateDisplay => _state.ToString();

		/// <summary>
		/// Gets current P&L from open position
		/// </summary>
		public double CurrentPositionPnL => (_currentPosition?.GrossPnLTicks ?? 0) * MGC_TICK_VALUE;
		#endregion

		#region Display Update
		private void UpdateDisplayInfo()
		{
			if (_symbol == null || _account == null)
				return;

			// Plot entry, profit target, and stop loss levels if in trade
			if (_state == TradingState.InTrade || _state == TradingState.ExitPending)
			{
				SetValue(_entryPrice, 0);           // EntryTarget line
				SetValue(_profitTarget, 1);         // ProfitTarget line
				SetValue(_stopLoss, 2);             // StopLoss line
			}

			// Display account information in chart text overlay
			// This would typically be done via a separate panel or text drawing API
			// For now, the information is available in debug logs and can be viewed
			// by hovering over the indicator or in the indicator panel
		}
		#endregion
	}
}
