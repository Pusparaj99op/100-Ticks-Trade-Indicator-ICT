// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.
// ICT 100 Ticks Trade Indicator — Full ICT Methodology Implementation
// Concepts: Order Blocks, FVGs, Liquidity Sweeps, MSS/BOS, Kill Zones,
//           Premium/Discount, OTE, PDH/PDL, Multi-Timeframe Analysis
// Timeframe: 5M | Symbol: MGC (Micro Gold Futures) | Exchange: CME

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace _100_Ticks_Trade_Indicator_ICT
{
    // ─────────────────────────────────────────────────────────────────────────
    // DATA STRUCTURES
    // ─────────────────────────────────────────────────────────────────────────

    internal enum SetupDirection { Long, Short }
    internal enum StructureType { BOS, MSS }
    internal enum ZoneStatus { Active, Mitigated, Broken }

    internal class SwingPoint
    {
        public double Price { get; set; }
        public DateTime Time { get; set; }
        public int BarIndex { get; set; }
        public bool IsHigh { get; set; }  // true = swing high, false = swing low
    }

    internal class OrderBlock
    {
        public double High { get; set; }
        public double Low { get; set; }
        public double BodyHigh { get; set; }    // Body top (Open or Close, whichever is higher)
        public double BodyLow { get; set; }     // Body bottom
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }   // Extended right as price hasn't returned
        public bool IsBullish { get; set; }
        public ZoneStatus Status { get; set; } = ZoneStatus.Active;
        public string Timeframe { get; set; } = "5M";
    }

    internal class FairValueGap
    {
        public double Top { get; set; }
        public double Bottom { get; set; }
        public double Midpoint => (Top + Bottom) / 2.0;
        public DateTime StartTime { get; set; }
        public bool IsBullish { get; set; }
        public ZoneStatus Status { get; set; } = ZoneStatus.Active;
    }

    internal class StructureEvent
    {
        public StructureType Type { get; set; }
        public SetupDirection Direction { get; set; }
        public double Level { get; set; }
        public DateTime Time { get; set; }
        public int BarIndex { get; set; }
    }

    internal class LiquiditySweep
    {
        public double Level { get; set; }
        public DateTime Time { get; set; }
        public bool IsBuySideSweep { get; set; }   // true = swept above (BSL), false = swept below (SSL)
        public string LevelLabel { get; set; } = "";
    }

    internal class TradeSetup
    {
        public SetupDirection Direction { get; set; }
        public double EntryPrice { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit { get; set; }
        public double TakeProfit2 { get; set; }    // 150-tick target
        public int ConfluenceScore { get; set; }
        public int MaxScore { get; set; } = 8;
        public DateTime DetectedAt { get; set; }
        public bool IsActive { get; set; }
        public string[] ConfluenceItems { get; set; } = Array.Empty<string>();
        public string[] MissingItems { get; set; } = Array.Empty<string>();
        public bool IsUpcomingSetup { get; set; }  // true = developing (not fully confirmed yet)
        public OrderBlock AnchorOB { get; set; }
        public FairValueGap AnchorFVG { get; set; }
    }

    internal class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public double ProfitTicks { get; set; }
        public double RealizedPnL { get; set; }
        public bool IsClosed { get; set; }
        public SetupDirection Direction { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MAIN INDICATOR
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 100 Ticks Trade Indicator — ICT Methodology
    ///
    /// Full ICT-based trading indicator for MGC (Micro Gold Futures) on a 5M chart.
    /// Implements: Order Blocks, Fair Value Gaps, Liquidity Sweeps, Market Structure
    /// Shifts (MSS/BOS), Kill Zones, Premium/Discount Zones, OTE, PDH/PDL, and
    /// multi-timeframe analysis (5M + 15M + 1H). Targets 100+ ticks per trade.
    ///
    /// Visual Elements:
    ///   • Green/Red rectangles = Bullish/Bearish Order Blocks
    ///   • Blue/Orange semi-transparent rectangles = Bullish/Bearish FVGs
    ///   • Dashed horizontal lines = PDH / PDL
    ///   • Purple background shading = Kill Zones
    ///   • ▲/▼ markers on bar = Setup signals
    ///   • On-chart HUD = Session / bias / score info
    /// </summary>
    public class _100_Ticks_Trade_Indicator_ICT : Indicator
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const double TICKS_PER_TRADE_TARGET  = 100;
        private const double TICKS_TP2               = 150;
        private const double MAX_RISK_TICKS          = 50;
        private const double DAILY_LOSS_LIMIT        = 100;
        private const int    MAX_TRADES_PER_DAY      = 4;
        private const int    FIXED_LOT_SIZE          = 1;
        private const double MGC_TICK_VALUE          = 1.00;  // $1 per tick for MGC
        private const string SYMBOL_NAME             = "MGC";
        private const string EXCHANGE_NAME           = "CME";

        private const int    SWING_LOOKBACK          = 5;     // bars each side for pivot
        private const int    OB_LOOKBACK             = 50;    // bars to scan for OBs
        private const int    FVG_LOOKBACK            = 80;    // bars to scan for FVGs
        private const int    MIN_CONFLUENCE_SCORE    = 5;     // out of 8
        private const int    DEVELOPING_SCORE        = 3;     // show "upcoming" label

        // ── Platform Fields ────────────────────────────────────────────────────
        private Account       _account         = null;
        private Symbol        _symbol          = null;
        private Position      _currentPosition = null;
        private HistoricalData _htfData15M     = null;
        private HistoricalData _htfData1H      = null;

        // ── ICT Structural Data ────────────────────────────────────────────────
        private List<SwingPoint>     _swingHighs      = new List<SwingPoint>();
        private List<SwingPoint>     _swingLows       = new List<SwingPoint>();
        private List<OrderBlock>     _orderBlocks     = new List<OrderBlock>();
        private List<FairValueGap>   _fvgs            = new List<FairValueGap>();
        private List<StructureEvent> _structureEvents = new List<StructureEvent>();
        private List<LiquiditySweep> _liquiditySweeps = new List<LiquiditySweep>();
        private List<TradeSetup>     _activeSetups    = new List<TradeSetup>();

        // ── Session / Day Tracking ─────────────────────────────────────────────
        private double   _pdh             = double.NaN;
        private double   _pdl             = double.NaN;
        private double   _sessionHigh     = double.NaN;
        private double   _sessionLow      = double.NaN;
        private DateTime _currentDay      = DateTime.MinValue;
        private double   _rangeHigh       = double.NaN;
        private double   _rangeLow        = double.NaN;
        private bool     _htfBullish      = false;  // 1H directional bias

        // ── Risk / P&L ─────────────────────────────────────────────────────────
        private int      _tradesExecToday = 0;
        private double   _dailyPnL        = 0;
        private double   _sessionBalance  = 0;
        private DateTime _sessionDate     = DateTime.MinValue;

        // ── Trade State ────────────────────────────────────────────────────────
        private TradeRecord _currentTrade  = null;
        private List<TradeRecord> _execTrades = new List<TradeRecord>();
        private double   _entryPrice      = 0;
        private double   _profitTarget    = 0;
        private double   _stopLoss        = 0;
        private bool     _inTrade         = false;

        // ── Cached GDI objects (created once in OnInit/fields) ─────────────────
        private Font  _hudFont      = null;
        private Font  _labelFont    = null;
        private Font  _smallFont    = null;
        private Pen   _dashPen      = null;

        // ── EST Timezone ───────────────────────────────────────────────────────
        private static readonly TimeZoneInfo _est = GetEstTimeZone();

        private static TimeZoneInfo GetEstTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════════════
        public _100_Ticks_Trade_Indicator_ICT() : base()
        {
            Name        = "100 Ticks ICT";
            Description = "ICT-based 100-tick setup indicator: OB, FVG, MSS/BOS, Kill Zones, Premium/Discount, OTE, PDH/PDL (5M chart)";

            // Signal line — used for marker placement (invisible value line)
            AddLineSeries("Signal", Color.Transparent, 1, LineStyle.Solid);

            SeparateWindow = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        // INPUT PARAMETERS
        // ══════════════════════════════════════════════════════════════════════
        #region Input Parameters

        [InputParameter("Account Name Filter", 0)]
        public string AccountNameFilter { get; set; } = "Lucid";

        [InputParameter("Enable Auto-Trading", 1)]
        public bool EnableTrading { get; set; } = false;

        [InputParameter("Target Ticks (TP1)", 2, 50, 50, 300, 10)]
        public double TargetTicks { get; set; } = TICKS_PER_TRADE_TARGET;

        [InputParameter("Target Ticks (TP2)", 3, 150, 50, 400, 10)]
        public double TargetTicks2 { get; set; } = TICKS_TP2;

        [InputParameter("Max Risk Ticks (SL)", 4, 50, 10, 150, 5)]
        public double MaxRiskTicks { get; set; } = MAX_RISK_TICKS;

        [InputParameter("Daily Loss Limit ($)", 5, 100, 50, 500, 50)]
        public double DailyLossLimit { get; set; } = DAILY_LOSS_LIMIT;

        [InputParameter("Max Trades Per Day", 6, 4, 1, 10, 1)]
        public int MaxTradesPerDay { get; set; } = MAX_TRADES_PER_DAY;

        [InputParameter("Lot Size", 7, 1, 1, 5, 1)]
        public int LotSize { get; set; } = FIXED_LOT_SIZE;

        [InputParameter("Min Confluence Score (0-8)", 8, 5, 1, 8, 1)]
        public int MinConfluence { get; set; } = MIN_CONFLUENCE_SCORE;

        [InputParameter("Swing Pivot Bars", 9, 5, 2, 15, 1)]
        public int SwingPivotBars { get; set; } = SWING_LOOKBACK;

        [InputParameter("Show Order Blocks", 10)]
        public bool ShowOrderBlocks { get; set; } = true;

        [InputParameter("Show Fair Value Gaps", 11)]
        public bool ShowFVGs { get; set; } = true;

        [InputParameter("Show Kill Zones", 12)]
        public bool ShowKillZones { get; set; } = true;

        [InputParameter("Show PDH/PDL Lines", 13)]
        public bool ShowPDHPDL { get; set; } = true;

        [InputParameter("Show HUD Panel", 14)]
        public bool ShowHUD { get; set; } = true;

        [InputParameter("Show HTF Order Blocks", 15)]
        public bool ShowHTFOrderBlocks { get; set; } = true;

        [InputParameter("OB Lookback Bars", 16, 50, 20, 200, 10)]
        public int OBLookback { get; set; } = OB_LOOKBACK;

        #endregion

        // ══════════════════════════════════════════════════════════════════════
        // LIFECYCLE — INIT
        // ══════════════════════════════════════════════════════════════════════
        protected override void OnInit()
        {
            try
            {
                // Resolve account
                _account = Core.Instance.Accounts
                    .FirstOrDefault(a => a.Name.Contains(AccountNameFilter));

                if (_account == null)
                    Log($"[WARN] Account not found for filter '{AccountNameFilter}' — display-only mode", LoggingLevel.System);

                // Resolve symbol
                _symbol = Core.Instance.Symbols
                    .FirstOrDefault(s => s.Name == SYMBOL_NAME && s.Exchange?.ExchangeName == EXCHANGE_NAME);

                if (_symbol == null)
                    Log($"[WARN] Symbol {SYMBOL_NAME} on {EXCHANGE_NAME} not found — using chart symbol", LoggingLevel.System);

                // Fall back to chart symbol
                if (_symbol == null)
                    _symbol = Symbol;

                // Session init
                _sessionDate    = DateTime.Today;
                _sessionBalance = _account?.Balance ?? 0;
                _tradesExecToday = 0;
                _dailyPnL       = 0;
                _currentDay     = DateTime.UtcNow.Date;

                // GDI resources
                _hudFont   = new Font("Consolas", 9f, FontStyle.Bold);
                _labelFont = new Font("Arial", 8f, FontStyle.Bold);
                _smallFont = new Font("Arial", 7f);
                _dashPen   = new Pen(Color.FromArgb(180, Color.Yellow), 1) { DashStyle = DashStyle.Dash };

                // Multi-timeframe data
                LoadHTFData();

                Log("[INIT] 100 Ticks ICT Indicator ready — 5M chart", LoggingLevel.Trading);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] OnInit: {ex.Message}", LoggingLevel.Error);
            }
        }

        private void LoadHTFData()
        {
            try
            {
                _htfData15M = Symbol.GetHistory(Period.MIN15, HistoryType.Bid, DateTime.UtcNow.AddDays(-10));
                _htfData1H  = Symbol.GetHistory(Period.HOUR1, HistoryType.Bid, DateTime.UtcNow.AddDays(-30));

                if (_htfData15M != null)
                    _htfData15M.NewHistoryItem += OnHTFUpdate;

                Log($"[MTF] Loaded: 15M={_htfData15M?.Count} bars, 1H={_htfData1H?.Count} bars", LoggingLevel.Trading);
            }
            catch (Exception ex)
            {
                Log($"[WARN] Could not load HTF data: {ex.Message}", LoggingLevel.System);
            }
        }

        private void OnHTFUpdate(object sender, HistoryEventArgs e)
        {
            // Recalculate HTF bias when higher timeframe bar closes
            CalculateHTFBias();
        }

        private void Log(string message, LoggingLevel level = LoggingLevel.Verbose)
        {
            System.Diagnostics.Debug.WriteLine($"[100TicksICT] {level}: {message}");
            try
            {
                Core.Instance?.Loggers?.Log(message, level, "100TicksICT");
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // LIFECYCLE — CLEAR / REMOVE
        // ══════════════════════════════════════════════════════════════════════
        protected override void OnClear()
        {
            if (_htfData15M != null)
            {
                _htfData15M.NewHistoryItem -= OnHTFUpdate;
                _htfData15M.Dispose();
                _htfData15M = null;
            }
            if (_htfData1H != null)
            {
                _htfData1H.Dispose();
                _htfData1H = null;
            }

            _hudFont?.Dispose();
            _labelFont?.Dispose();
            _smallFont?.Dispose();
            _dashPen?.Dispose();

            _orderBlocks.Clear();
            _fvgs.Clear();
            _swingHighs.Clear();
            _swingLows.Clear();
            _structureEvents.Clear();
            _liquiditySweeps.Clear();
            _activeSetups.Clear();
        }

        // ══════════════════════════════════════════════════════════════════════
        // LIFECYCLE — ON UPDATE (main tick/bar calculation)
        // ══════════════════════════════════════════════════════════════════════
        protected override void OnUpdate(UpdateArgs args)
        {
            try
            {
                if (Count < 20) return;

                // Session reset check
                CheckSessionReset();

                // Only do heavy recalculation on new bar (not every tick)
                bool isNewBar = args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar;

                if (isNewBar)
                {
                    // Update PDH/PDL and session levels
                    UpdateSessionLevels();

                    // Detect swing points
                    DetectSwingPoints();

                    // Detect Market Structure Shifts and Breaks
                    DetectStructure();

                    // Detect Order Blocks
                    DetectOrderBlocks();

                    // Detect Fair Value Gaps
                    DetectFVGs();

                    // Detect Liquidity Sweeps
                    DetectLiquiditySweeps();

                    // Update zone status (mitigated / broken)
                    UpdateZoneStatuses();

                    // Calculate HTF bias
                    CalculateHTFBias();

                    // Calculate Premium/Discount range
                    CalculateTradingRange();

                    // Run confluence engine and generate/update setups
                    EvaluateSetups();

                    // Position tracking / risk management
                    UpdatePositionState();
                    ProcessTradingSignals();
                }

                // Update signal line (invisible, needed for markers to anchor)
                SetValue(double.NaN, 0);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] OnUpdate: {ex.Message}", LoggingLevel.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SESSION MANAGEMENT
        // ══════════════════════════════════════════════════════════════════════
        private void CheckSessionReset()
        {
            if (DateTime.Today != _sessionDate)
            {
                _pdh             = _sessionHigh;
                _pdl             = _sessionLow;
                _sessionDate     = DateTime.Today;
                _sessionBalance  = _account?.Balance ?? 0;
                _tradesExecToday = 0;
                _dailyPnL        = 0;
                _sessionHigh     = double.NaN;
                _sessionLow      = double.NaN;
                // Prune old data structures
                PruneOldZones();
                Log($"[SESSION] New day. PDH={_pdh:F1} PDL={_pdl:F1}", LoggingLevel.Trading);
            }
        }

        private void UpdateSessionLevels()
        {
            double h = High(0);
            double l = Low(0);
            if (double.IsNaN(_sessionHigh) || h > _sessionHigh) _sessionHigh = h;
            if (double.IsNaN(_sessionLow)  || l < _sessionLow)  _sessionLow  = l;
        }

        private void PruneOldZones()
        {
            // Keep only zones from the past 3 days
            var cutoff = DateTime.UtcNow.AddDays(-3);
            _orderBlocks.RemoveAll(o => o.StartTime < cutoff);
            _fvgs.RemoveAll(f => f.StartTime < cutoff);
            _structureEvents.RemoveAll(s => s.Time < cutoff);
            _liquiditySweeps.RemoveAll(ls => ls.Time < cutoff);

            // Keep max N swing points
            if (_swingHighs.Count > 100) _swingHighs.RemoveRange(0, _swingHighs.Count - 100);
            if (_swingLows.Count  > 100) _swingLows.RemoveRange(0, _swingLows.Count - 100);
        }

        // ══════════════════════════════════════════════════════════════════════
        // SWING POINT DETECTION
        // ══════════════════════════════════════════════════════════════════════
        private void DetectSwingPoints()
        {
            int pivot = SwingPivotBars;
            int i     = pivot; // confirmed pivot is pivot bars back

            if (Count < pivot * 2 + 2) return;

            double pivotHigh = High(i);
            double pivotLow  = Low(i);
            bool isSwingHigh = true;
            bool isSwingLow  = true;

            for (int b = 1; b <= pivot; b++)
            {
                if (High(i - b) >= pivotHigh) isSwingHigh = false;
                if (High(i + b) >= pivotHigh) isSwingHigh = false;
                if (Low(i - b)  <= pivotLow)  isSwingLow  = false;
                if (Low(i + b)  <= pivotLow)  isSwingLow  = false;
            }

            DateTime barTime = Time(i);

            if (isSwingHigh && !_swingHighs.Any(s => s.BarIndex == (Count - 1 - i)))
            {
                _swingHighs.Add(new SwingPoint { Price = pivotHigh, Time = barTime, BarIndex = Count - 1 - i, IsHigh = true });
            }

            if (isSwingLow && !_swingLows.Any(s => s.BarIndex == (Count - 1 - i)))
            {
                _swingLows.Add(new SwingPoint { Price = pivotLow, Time = barTime, BarIndex = Count - 1 - i, IsHigh = false });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // MARKET STRUCTURE — MSS / BOS
        // ══════════════════════════════════════════════════════════════════════
        private void DetectStructure()
        {
            if (_swingHighs.Count < 2 || _swingLows.Count < 2) return;

            double curClose = Close(0);
            DateTime curTime = Time(0);
            int curBar = Count - 1;

            // Get most recent confirmed swing points
            var lastHigh = _swingHighs.OrderByDescending(s => s.BarIndex).FirstOrDefault();
            var lastLow  = _swingLows.OrderByDescending(s => s.BarIndex).FirstOrDefault();
            var prevHigh = _swingHighs.OrderByDescending(s => s.BarIndex).Skip(1).FirstOrDefault();
            var prevLow  = _swingLows.OrderByDescending(s => s.BarIndex).Skip(1).FirstOrDefault();

            if (lastHigh == null || lastLow == null || prevHigh == null || prevLow == null) return;

            bool alreadyLogged = _structureEvents.Any(se => se.BarIndex == curBar);
            if (alreadyLogged) return;

            // ── Bullish BOS: Close above prior swing high ──────────────────
            if (curClose > lastHigh.Price && IsDisplacement(0))
            {
                // Is it MSS (reversal after downtrend) or BOS (continuation after uptrend)?
                bool wasBearish = lastHigh.Price < prevHigh.Price;
                var type = wasBearish ? StructureType.MSS : StructureType.BOS;

                _structureEvents.Add(new StructureEvent
                {
                    Type      = type,
                    Direction = SetupDirection.Long,
                    Level     = lastHigh.Price,
                    Time      = curTime,
                    BarIndex  = curBar
                });

                PlaceSetupMarker(0, SetupDirection.Long, type);
            }

            // ── Bearish BOS: Close below prior swing low ───────────────────
            else if (curClose < lastLow.Price && IsDisplacement(0))
            {
                bool wasBullish = lastLow.Price > prevLow.Price;
                var type = wasBullish ? StructureType.MSS : StructureType.BOS;

                _structureEvents.Add(new StructureEvent
                {
                    Type      = type,
                    Direction = SetupDirection.Short,
                    Level     = lastLow.Price,
                    Time      = curTime,
                    BarIndex  = curBar
                });

                PlaceSetupMarker(0, SetupDirection.Short, type);
            }
        }

        private bool IsDisplacement(int barOffset)
        {
            double body  = Math.Abs(Close(barOffset) - Open(barOffset));
            double range = High(barOffset) - Low(barOffset);
            if (range < 0.001) return false;

            // Average range of last 5 bars
            double avgRange = 0;
            int samples = Math.Min(5, Count - barOffset - 1);
            for (int b = 1; b <= samples; b++)
                avgRange += (High(barOffset + b) - Low(barOffset + b));
            if (samples > 0) avgRange /= samples;
            if (avgRange < 0.001) return false;

            return (body / range >= 0.65) && (range >= 1.5 * avgRange);
        }

        private void PlaceSetupMarker(int barOffset, SetupDirection dir, StructureType structType)
        {
            try
            {
                Color col   = dir == SetupDirection.Long ? Color.Lime : Color.OrangeRed;
                var   icon  = dir == SetupDirection.Long
                    ? IndicatorLineMarkerIconType.UpArrow
                    : IndicatorLineMarkerIconType.DownArrow;

                // For long: place below the bar; for short: above the bar
                if (dir == SetupDirection.Long)
                    LinesSeries[0].SetMarker(0, new IndicatorLineMarker(col, bottomIcon: icon));
                else
                    LinesSeries[0].SetMarker(0, new IndicatorLineMarker(col, upperIcon: icon));
            }
            catch { /* markers optional */ }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ORDER BLOCK DETECTION
        // ══════════════════════════════════════════════════════════════════════
        private void DetectOrderBlocks()
        {
            int lookback = Math.Min(OBLookback, Count - 2);

            for (int i = 2; i < lookback; i++)
            {
                // ── Bullish OB: last bearish candle before bullish displacement ──
                bool isBearishCandle = Close(i) < Open(i);
                bool nextIsBullish   = Close(i - 1) > Open(i - 1);
                bool displacement    = IsDisplacement(i - 1);

                if (isBearishCandle && nextIsBullish && displacement)
                {
                    DateTime obTime = Time(i);
                    if (!_orderBlocks.Any(o => o.StartTime == obTime && o.IsBullish))
                    {
                        _orderBlocks.Add(new OrderBlock
                        {
                            High      = High(i),
                            Low       = Low(i),
                            BodyHigh  = Math.Max(Open(i), Close(i)),
                            BodyLow   = Math.Min(Open(i), Close(i)),
                            StartTime = obTime,
                            EndTime   = DateTime.UtcNow.AddDays(1),
                            IsBullish = true,
                            Status    = ZoneStatus.Active,
                            Timeframe = "5M"
                        });
                    }
                }

                // ── Bearish OB: last bullish candle before bearish displacement ──
                bool isBullishCandle = Close(i) > Open(i);
                bool nextIsBearish   = Close(i - 1) < Open(i - 1);
                bool displacementDn  = IsDisplacement(i - 1);

                if (isBullishCandle && nextIsBearish && displacementDn)
                {
                    DateTime obTime = Time(i);
                    if (!_orderBlocks.Any(o => o.StartTime == obTime && !o.IsBullish))
                    {
                        _orderBlocks.Add(new OrderBlock
                        {
                            High      = High(i),
                            Low       = Low(i),
                            BodyHigh  = Math.Max(Open(i), Close(i)),
                            BodyLow   = Math.Min(Open(i), Close(i)),
                            StartTime = obTime,
                            EndTime   = DateTime.UtcNow.AddDays(1),
                            IsBullish = false,
                            Status    = ZoneStatus.Active,
                            Timeframe = "5M"
                        });
                    }
                }
            }

            // ── HTF Order Blocks from 15M data ─────────────────────────────
            if (ShowHTFOrderBlocks) DetectHTFOrderBlocks();
        }

        private void DetectHTFOrderBlocks()
        {
            if (_htfData15M == null || _htfData15M.Count < 5) return;

            int lookback = Math.Min(30, _htfData15M.Count - 2);
            for (int i = 2; i < lookback; i++)
            {
                try
                {
                    var bar0 = (HistoryItemBar)_htfData15M[i];
                    var bar1 = (HistoryItemBar)_htfData15M[i - 1];

                    double c0 = bar0[PriceType.Close], o0 = bar0[PriceType.Open];
                    double c1 = bar1[PriceType.Close], o1 = bar1[PriceType.Open];
                    double h0 = bar0[PriceType.High],  l0 = bar0[PriceType.Low];
                    DateTime t0 = bar0.TimeLeft;

                    bool bearishOBCandidate = c0 < o0 && c1 > o1;
                    bool bullishOBCandidate = c0 > o0 && c1 < o1;
                    double range1 = bar1[PriceType.High] - bar1[PriceType.Low];

                    if (bearishOBCandidate && !_orderBlocks.Any(ob => ob.StartTime == t0 && ob.IsBullish && ob.Timeframe == "15M"))
                    {
                        _orderBlocks.Add(new OrderBlock
                        {
                            High = h0, Low = l0,
                            BodyHigh = Math.Max(o0, c0), BodyLow = Math.Min(o0, c0),
                            StartTime = t0, EndTime = DateTime.UtcNow.AddDays(1),
                            IsBullish = true, Status = ZoneStatus.Active, Timeframe = "15M"
                        });
                    }

                    if (bullishOBCandidate && !_orderBlocks.Any(ob => ob.StartTime == t0 && !ob.IsBullish && ob.Timeframe == "15M"))
                    {
                        _orderBlocks.Add(new OrderBlock
                        {
                            High = h0, Low = l0,
                            BodyHigh = Math.Max(o0, c0), BodyLow = Math.Min(o0, c0),
                            StartTime = t0, EndTime = DateTime.UtcNow.AddDays(1),
                            IsBullish = false, Status = ZoneStatus.Active, Timeframe = "15M"
                        });
                    }
                }
                catch { /* skip malformed bars */ }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // FAIR VALUE GAP DETECTION
        // ══════════════════════════════════════════════════════════════════════
        private void DetectFVGs()
        {
            int lookback = Math.Min(FVG_LOOKBACK, Count - 3);

            for (int i = 2; i < lookback; i++)
            {
                // Candle indexing: bar at i is candle 1, i-1 is candle 2 (middle), i-2 is candle 3
                double c1High = High(i);
                double c1Low  = Low(i);
                double c3High = High(i - 2);
                double c3Low  = Low(i - 2);
                DateTime startTime = Time(i);

                // ── Bullish FVG: gap between C1 high and C3 low ─────────────
                if (c1High < c3Low)
                {
                    if (!_fvgs.Any(f => f.StartTime == startTime && f.IsBullish))
                    {
                        _fvgs.Add(new FairValueGap
                        {
                            Top       = c3Low,
                            Bottom    = c1High,
                            StartTime = startTime,
                            IsBullish = true,
                            Status    = ZoneStatus.Active
                        });
                    }
                }

                // ── Bearish FVG: gap between C3 high and C1 low ─────────────
                if (c3High < c1Low)
                {
                    if (!_fvgs.Any(f => f.StartTime == startTime && !f.IsBullish))
                    {
                        _fvgs.Add(new FairValueGap
                        {
                            Top       = c1Low,
                            Bottom    = c3High,
                            StartTime = startTime,
                            IsBullish = false,
                            Status    = ZoneStatus.Active
                        });
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // LIQUIDITY SWEEP DETECTION
        // ══════════════════════════════════════════════════════════════════════
        private void DetectLiquiditySweeps()
        {
            double curHigh  = High(0);
            double curLow   = Low(0);
            double curClose = Close(0);
            DateTime curTime = Time(0);

            // ── Buy-Side Liquidity Sweep (swept above, closed below) ───────
            var recentHighs = _swingHighs.OrderByDescending(s => s.BarIndex).Take(3).ToList();
            foreach (var sh in recentHighs)
            {
                if (curHigh > sh.Price && curClose < sh.Price)
                {
                    if (!_liquiditySweeps.Any(ls => ls.Time == curTime && ls.IsBuySideSweep))
                    {
                        _liquiditySweeps.Add(new LiquiditySweep
                        {
                            Level          = sh.Price,
                            Time           = curTime,
                            IsBuySideSweep = true,
                            LevelLabel     = "BSL Sweep"
                        });
                    }
                }
            }

            // PDH sweep
            if (!double.IsNaN(_pdh) && curHigh > _pdh && curClose < _pdh)
            {
                if (!_liquiditySweeps.Any(ls => ls.Time == curTime && ls.IsBuySideSweep))
                {
                    _liquiditySweeps.Add(new LiquiditySweep
                    {
                        Level = _pdh, Time = curTime, IsBuySideSweep = true, LevelLabel = "PDH Sweep"
                    });
                }
            }

            // ── Sell-Side Liquidity Sweep (swept below, closed above) ──────
            var recentLows = _swingLows.OrderByDescending(s => s.BarIndex).Take(3).ToList();
            foreach (var sl in recentLows)
            {
                if (curLow < sl.Price && curClose > sl.Price)
                {
                    if (!_liquiditySweeps.Any(ls => ls.Time == curTime && !ls.IsBuySideSweep))
                    {
                        _liquiditySweeps.Add(new LiquiditySweep
                        {
                            Level          = sl.Price,
                            Time           = curTime,
                            IsBuySideSweep = false,
                            LevelLabel     = "SSL Sweep"
                        });
                    }
                }
            }

            // PDL sweep
            if (!double.IsNaN(_pdl) && curLow < _pdl && curClose > _pdl)
            {
                if (!_liquiditySweeps.Any(ls => ls.Time == curTime && !ls.IsBuySideSweep))
                {
                    _liquiditySweeps.Add(new LiquiditySweep
                    {
                        Level = _pdl, Time = curTime, IsBuySideSweep = false, LevelLabel = "PDL Sweep"
                    });
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ZONE STATUS UPDATES (mitigated / broken)
        // ══════════════════════════════════════════════════════════════════════
        private void UpdateZoneStatuses()
        {
            double curHigh  = High(0);
            double curLow   = Low(0);
            double curClose = Close(0);
            double curOpen  = Open(0);

            foreach (var ob in _orderBlocks.Where(o => o.Status == ZoneStatus.Active))
            {
                if (ob.IsBullish)
                {
                    // Bullish OB broken: close below wick low
                    if (curClose < ob.Low)
                        ob.Status = ZoneStatus.Broken;
                    // Mitigated (price touched body zone but held)
                    else if (curLow <= ob.BodyHigh && curClose > ob.BodyLow)
                        ob.Status = ZoneStatus.Mitigated;
                }
                else
                {
                    // Bearish OB broken: close above wick high
                    if (curClose > ob.High)
                        ob.Status = ZoneStatus.Broken;
                    else if (curHigh >= ob.BodyLow && curClose < ob.BodyHigh)
                        ob.Status = ZoneStatus.Mitigated;
                }
            }

            foreach (var fvg in _fvgs.Where(f => f.Status == ZoneStatus.Active))
            {
                // FVG filled: price fully traded through the gap
                if (fvg.IsBullish && curLow <= fvg.Bottom)
                    fvg.Status = ZoneStatus.Broken;
                else if (!fvg.IsBullish && curHigh >= fvg.Top)
                    fvg.Status = ZoneStatus.Broken;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // HTF BIAS CALCULATION
        // ══════════════════════════════════════════════════════════════════════
        private void CalculateHTFBias()
        {
            if (_htfData1H == null || _htfData1H.Count < 5)
            {
                // Fall back to 5M structure
                _htfBullish = _swingLows.Count >= 2 &&
                    _swingLows.OrderByDescending(s => s.BarIndex).Skip(1).First().Price <
                    _swingLows.OrderByDescending(s => s.BarIndex).First().Price;
                return;
            }

            try
            {
                // Simple: last 5 1H bars — is the trend up or down?
                int bars = Math.Min(5, _htfData1H.Count);
                double firstClose = ((HistoryItemBar)_htfData1H[bars - 1])[PriceType.Close];
                double lastClose  = ((HistoryItemBar)_htfData1H[0])[PriceType.Close];
                _htfBullish = lastClose > firstClose;
            }
            catch
            {
                _htfBullish = true; // default
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // PREMIUM / DISCOUNT RANGE
        // ══════════════════════════════════════════════════════════════════════
        private void CalculateTradingRange()
        {
            // Use the most significant recent swing high/low pair
            var topSwing = _swingHighs.OrderByDescending(s => s.Price).FirstOrDefault();
            var botSwing = _swingLows.OrderByDescending(s => s.BarIndex).FirstOrDefault();

            if (topSwing != null && botSwing != null && topSwing.Price > botSwing.Price)
            {
                _rangeHigh = topSwing.Price;
                _rangeLow  = botSwing.Price;
            }
        }

        private double GetEquilibrium() => (_rangeHigh + _rangeLow) / 2.0;

        private bool IsInDiscount(double price)
        {
            if (double.IsNaN(_rangeHigh) || double.IsNaN(_rangeLow)) return false;
            return price < GetEquilibrium();
        }

        private bool IsInPremium(double price)
        {
            if (double.IsNaN(_rangeHigh) || double.IsNaN(_rangeLow)) return false;
            return price > GetEquilibrium();
        }

        // OTE zone: 61.8%–79% retracement
        private (double top, double bottom) GetOTEZone(SetupDirection dir)
        {
            if (double.IsNaN(_rangeHigh) || double.IsNaN(_rangeLow))
                return (double.NaN, double.NaN);

            double span = _rangeHigh - _rangeLow;
            if (dir == SetupDirection.Long)
            {
                double top    = _rangeHigh - span * 0.618;
                double bottom = _rangeHigh - span * 0.79;
                return (top, bottom);
            }
            else
            {
                double top    = _rangeLow + span * 0.79;
                double bottom = _rangeLow + span * 0.618;
                return (top, bottom);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // KILL ZONE DETECTION
        // ══════════════════════════════════════════════════════════════════════
        private bool IsInKillZone(DateTime utcTime, out string zoneName)
        {
            zoneName = "";
            try
            {
                DateTime est   = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _est);
                double   t     = est.Hour + est.Minute / 60.0;

                if (t >= 20.0 || t < 0.5)         { zoneName = "Asian KZ";      return false; } // Asian — don't trade
                if (t >= 2.0 && t < 5.0)           { zoneName = "London KZ";     return true;  }
                if (t >= 7.0 && t < 10.0)          { zoneName = "New York KZ";   return true;  }
                if (t >= 11.0 && t < 13.0)         { zoneName = "London Close";  return true;  }
            }
            catch { /* timezone conversion failed */ }
            return false;
        }

        private string GetSessionLabel(DateTime utcTime)
        {
            try
            {
                DateTime est = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _est);
                double t = est.Hour + est.Minute / 60.0;
                if (t >= 20.0 || t < 2.0)  return "Asian";
                if (t >= 2.0 && t < 7.0)   return "London";
                if (t >= 7.0 && t < 14.0)  return "New York";
                return "Off-Hours";
            }
            catch { return "?"; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CONFLUENCE SCORING ENGINE
        // ══════════════════════════════════════════════════════════════════════
        private void EvaluateSetups()
        {
            // Expire old setups (older than 2 hours)
            _activeSetups.RemoveAll(s => (DateTime.UtcNow - s.DetectedAt).TotalHours > 2);

            double curPrice = Close(0);
            DateTime curTime = Time(0);
            double tickSize = Symbol?.TickSize ?? 0.1;

            // Check for both Long and Short setups
            EvaluateDirectionalSetup(SetupDirection.Long,  curPrice, curTime, tickSize);
            EvaluateDirectionalSetup(SetupDirection.Short, curPrice, curTime, tickSize);
        }

        private void EvaluateDirectionalSetup(SetupDirection dir, double curPrice, DateTime curTime, double tickSize)
        {
            var hitList   = new List<string>();
            var missList  = new List<string>();

            // ── Criterion 1: Inside a Kill Zone ───────────────────────────
            string kzName;
            bool inKZ = IsInKillZone(curTime, out kzName);
            if (inKZ)  hitList.Add($"Kill Zone ({kzName})");
            else       missList.Add("Kill Zone");

            // ── Criterion 2: HTF Bias aligned ────────────────────────────
            bool biasOK = dir == SetupDirection.Long ? _htfBullish : !_htfBullish;
            if (biasOK)  hitList.Add("HTF Bias (1H)");
            else         missList.Add("HTF Bias (1H)");

            // ── Criterion 3: Price in Premium/Discount zone ───────────────
            bool zoneOK = dir == SetupDirection.Long ? IsInDiscount(curPrice) : IsInPremium(curPrice);
            if (zoneOK)  hitList.Add(dir == SetupDirection.Long ? "Discount Zone" : "Premium Zone");
            else         missList.Add(dir == SetupDirection.Long ? "Discount Zone" : "Premium Zone");

            // ── Criterion 4: Recent Liquidity Sweep ──────────────────────
            var recentSweep = _liquiditySweeps
                .Where(ls => (curTime - ls.Time).TotalMinutes <= 60)
                .Where(ls => dir == SetupDirection.Long ? !ls.IsBuySideSweep : ls.IsBuySideSweep)
                .OrderByDescending(ls => ls.Time)
                .FirstOrDefault();
            if (recentSweep != null) hitList.Add(recentSweep.LevelLabel);
            else                     missList.Add("Liquidity Sweep");

            // ── Criterion 5: MSS confirmed in direction ───────────────────
            var recentMSS = _structureEvents
                .Where(se => (curTime - se.Time).TotalMinutes <= 60)
                .Where(se => se.Direction == dir && se.Type == StructureType.MSS)
                .OrderByDescending(se => se.Time)
                .FirstOrDefault();
            if (recentMSS != null) hitList.Add("MSS Confirmed");
            else
            {
                var recentBOS = _structureEvents
                    .Where(se => (curTime - se.Time).TotalMinutes <= 30)
                    .Where(se => se.Direction == dir && se.Type == StructureType.BOS)
                    .OrderByDescending(se => se.Time)
                    .FirstOrDefault();
                if (recentBOS != null) hitList.Add("BOS Confirmed");
                else missList.Add("MSS/BOS");
            }

            // ── Criterion 6: Active Order Block near price ────────────────
            var nearOB = _orderBlocks
                .Where(ob => ob.Status == ZoneStatus.Active && ob.IsBullish == (dir == SetupDirection.Long))
                .Where(ob => dir == SetupDirection.Long
                    ? (curPrice <= ob.BodyHigh + 20 * tickSize && curPrice >= ob.Low - 5 * tickSize)
                    : (curPrice >= ob.BodyLow - 20 * tickSize && curPrice <= ob.High + 5 * tickSize))
                .OrderByDescending(ob => ob.StartTime)
                .FirstOrDefault();
            if (nearOB != null) hitList.Add($"Order Block ({nearOB.Timeframe})");
            else                missList.Add("Order Block");

            // ── Criterion 7: Active FVG near price ───────────────────────
            var nearFVG = _fvgs
                .Where(f => f.Status == ZoneStatus.Active && f.IsBullish == (dir == SetupDirection.Long))
                .Where(f => dir == SetupDirection.Long
                    ? (curPrice <= f.Top + 15 * tickSize && curPrice >= f.Bottom - 5 * tickSize)
                    : (curPrice >= f.Bottom - 15 * tickSize && curPrice <= f.Top + 5 * tickSize))
                .OrderByDescending(f => f.StartTime)
                .FirstOrDefault();
            if (nearFVG != null) hitList.Add("Fair Value Gap");
            else                 missList.Add("Fair Value Gap");

            // ── Criterion 8: Within OTE Zone ─────────────────────────────
            var (oteTop, oteBotm) = GetOTEZone(dir);
            bool inOTE = !double.IsNaN(oteTop) && curPrice >= oteBotm && curPrice <= oteTop;
            if (inOTE)  hitList.Add("OTE Zone (61.8%-79%)");
            else        missList.Add("OTE Zone");

            int score = hitList.Count;

            // ── Skip if we already have a recent equivalent setup ─────────
            bool duplicate = _activeSetups.Any(s =>
                s.Direction == dir && (curTime - s.DetectedAt).TotalMinutes < 15);
            if (duplicate) return;

            // ── Only register if score meets developing threshold ─────────
            if (score < DEVELOPING_SCORE) return;

            // ── Calculate entry, SL, TP ───────────────────────────────────
            double entry = curPrice;
            double sl, tp1, tp2;

            if (nearOB != null)
                entry = dir == SetupDirection.Long ? nearOB.BodyLow + tickSize : nearOB.BodyHigh - tickSize;
            else if (nearFVG != null)
                entry = dir == SetupDirection.Long ? nearFVG.Midpoint : nearFVG.Midpoint;

            if (dir == SetupDirection.Long)
            {
                sl  = nearOB != null ? nearOB.Low - tickSize : entry - MaxRiskTicks * tickSize;
                tp1 = entry + TargetTicks  * tickSize;
                tp2 = entry + TargetTicks2 * tickSize;
            }
            else
            {
                sl  = nearOB != null ? nearOB.High + tickSize : entry + MaxRiskTicks * tickSize;
                tp1 = entry - TargetTicks  * tickSize;
                tp2 = entry - TargetTicks2 * tickSize;
            }

            var setup = new TradeSetup
            {
                Direction      = dir,
                EntryPrice     = entry,
                StopLoss       = sl,
                TakeProfit     = tp1,
                TakeProfit2    = tp2,
                ConfluenceScore = score,
                MaxScore       = 8,
                DetectedAt     = curTime,
                IsActive       = score >= MinConfluence,
                IsUpcomingSetup = score < MinConfluence,
                ConfluenceItems = hitList.ToArray(),
                MissingItems   = missList.ToArray(),
                AnchorOB       = nearOB,
                AnchorFVG      = nearFVG
            };

            _activeSetups.Add(setup);

            if (score >= MinConfluence)
            {
                Log($"[SETUP] {dir} | Score={score}/8 | Entry={entry:F1} | SL={sl:F1} | TP={tp1:F1} | {string.Join(", ", hitList)}", LoggingLevel.Trading);
                // Place setup marker on chart bar
                PlaceSetupMarker(0, dir, StructureType.MSS);

                // Auto-trade if enabled
                if (EnableTrading && IsRiskAvailable())
                    SubmitEntryOrder(setup);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // POSITION TRACKING & TRADE PROCESSING
        // ══════════════════════════════════════════════════════════════════════
        private void UpdatePositionState()
        {
            _currentPosition = Core.Instance.Positions
                .FirstOrDefault(p => p.Symbol?.Name == _symbol?.Name && p.Account == _account);
        }

        private void ProcessTradingSignals()
        {
            if (_currentPosition == null || _currentPosition.Quantity == 0)
            {
                if (_inTrade)
                {
                    _inTrade = false;
                    _tradesExecToday++;
                    if (_currentTrade != null)
                    {
                        _currentTrade.IsClosed = true;
                        _execTrades.Add(_currentTrade);
                        _currentTrade = null;
                    }
                }
                return;
            }

            if (!_inTrade) return;

            // Manual TP/SL check for visual-only mode
            double bid   = Bid();
            double ask   = Ask();
            double price = _currentPosition.Quantity > 0 ? bid : ask;

            if (_currentPosition.Quantity > 0 && price >= _profitTarget && EnableTrading)
                SubmitExitOrder("TP1");
            else if (_currentPosition.Quantity > 0 && price <= _stopLoss && EnableTrading)
                SubmitExitOrder("SL");
            else if (_currentPosition.Quantity < 0 && price <= _profitTarget && EnableTrading)
                SubmitExitOrder("TP1");
            else if (_currentPosition.Quantity < 0 && price >= _stopLoss && EnableTrading)
                SubmitExitOrder("SL");
        }

        // ══════════════════════════════════════════════════════════════════════
        // ORDER MANAGEMENT
        // ══════════════════════════════════════════════════════════════════════
        private bool IsRiskAvailable()
        {
            if (_tradesExecToday >= MaxTradesPerDay) return false;
            if (_dailyPnL <= -DailyLossLimit) return false;
            return true;
        }

        private void SubmitEntryOrder(TradeSetup setup)
        {
            if (_account == null || _symbol == null) return;
            try
            {
                var p = new PlaceOrderRequestParameters
                {
                    Symbol      = _symbol,
                    Account     = _account,
                    Side        = setup.Direction == SetupDirection.Long ? Side.Buy : Side.Sell,
                    OrderTypeId = OrderType.Market,
                    Quantity    = LotSize,
                    Comment     = $"ICT_{setup.Direction}_Score{setup.ConfluenceScore}_TP{TargetTicks}"
                };

                Core.Instance.PlaceOrder(p);

                _entryPrice   = setup.EntryPrice;
                _profitTarget = setup.TakeProfit;
                _stopLoss     = setup.StopLoss;
                _inTrade      = true;
                _currentTrade = new TradeRecord
                {
                    EntryTime  = DateTime.UtcNow,
                    EntryPrice = setup.EntryPrice,
                    Direction  = setup.Direction
                };
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Entry order: {ex.Message}", LoggingLevel.Error);
            }
        }

        private void SubmitExitOrder(string reason)
        {
            if (_currentPosition == null || _account == null || _symbol == null) return;
            try
            {
                var p = new PlaceOrderRequestParameters
                {
                    Symbol      = _symbol,
                    Account     = _account,
                    Side        = _currentPosition.Quantity > 0 ? Side.Sell : Side.Buy,
                    OrderTypeId = OrderType.Market,
                    Quantity    = Math.Abs(_currentPosition.Quantity),
                    Comment     = $"ICT_Exit_{reason}"
                };
                Core.Instance.PlaceOrder(p);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Exit order: {ex.Message}", LoggingLevel.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CHART PAINTING — ALL VISUAL ELEMENTS
        // ══════════════════════════════════════════════════════════════════════
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            try
            {
                Graphics gr = args.Graphics;
                var converter = CurrentChart?.MainWindow?.CoordinatesConverter;
                if (converter == null || gr == null) return;

                var chartBounds = args.Rectangle;
                int chartWidth  = chartBounds.Width;
                int chartHeight = chartBounds.Height;

                // Draw Kill Zones (background shading — first so it's behind everything)
                if (ShowKillZones)
                    DrawKillZones(gr, converter, chartWidth, chartHeight);

                // Draw PDH / PDL lines
                if (ShowPDHPDL)
                    DrawPDHPDL(gr, converter, chartWidth);

                // Draw Order Blocks
                if (ShowOrderBlocks)
                    DrawOrderBlocks(gr, converter, chartWidth);

                // Draw Fair Value Gaps
                if (ShowFVGs)
                    DrawFairValueGaps(gr, converter, chartWidth);

                // Draw Liquidity Sweep markers
                DrawLiquiditySweeps(gr, converter);

                // Draw Structure Events (MSS / BOS labels)
                DrawStructureEvents(gr, converter);

                // Draw Active Setup zones (entry / SL / TP lines)
                DrawActiveSetups(gr, converter, chartWidth);

                // Draw Premium/Discount zones
                DrawPremiumDiscountZones(gr, converter, chartWidth);

                // Draw HUD panel (last — on top of everything)
                if (ShowHUD)
                    DrawHUD(gr, chartWidth, chartHeight);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] OnPaintChart: {ex.Message}", LoggingLevel.Error);
            }
        }

        // ── Kill Zone Background Shading ───────────────────────────────────────
        private void DrawKillZones(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth, int chartHeight)
        {
            if (Count < 2) return;

            // Shade the visible portion of each bar that falls in a kill zone
            int visibleBars = Math.Min(200, Count);
            int? kzStart = null;

            for (int i = visibleBars - 1; i >= 0; i--)
            {
                DateTime barTime = Time(i);
                string kzName;
                bool inKZ = IsInKillZone(barTime, out kzName);

                if (inKZ && kzStart == null) kzStart = i;
                if (!inKZ && kzStart != null)
                {
                    // Draw shaded rectangle from kzStart to current bar
                    int xLeft  = (int)converter.GetChartX(Time(kzStart.Value));
                    int xRight = (int)converter.GetChartX(barTime);

                    Color kzColor = kzName.Contains("London")
                        ? Color.FromArgb(18, 100, 200, 255)    // Blue tint for London
                        : Color.FromArgb(18, 255, 200, 50);    // Gold tint for NY

                    using (var brush = new SolidBrush(kzColor))
                        gr.FillRectangle(brush, Math.Min(xLeft, xRight), 0, Math.Abs(xRight - xLeft), chartHeight);

                    // Kill zone label
                    try
                    {
                        using (var lf = new Font("Arial", 7f))
                        using (var lb = new SolidBrush(Color.FromArgb(80, 200, 200, 200)))
                            gr.DrawString(kzName, lf, lb, Math.Min(xLeft, xRight) + 2, 4);
                    }
                    catch { }

                    kzStart = null;
                }
            }
        }

        // ── PDH / PDL Lines ────────────────────────────────────────────────────
        private void DrawPDHPDL(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth)
        {
            if (!double.IsNaN(_pdh))
            {
                int yPDH = (int)converter.GetChartY(_pdh);
                using (var pen = new Pen(Color.FromArgb(200, 50, 255, 80), 1) { DashStyle = DashStyle.Dash })
                    gr.DrawLine(pen, 0, yPDH, chartWidth, yPDH);
                DrawLabel(gr, "PDH", 4, yPDH - 12, Color.FromArgb(200, 50, 255, 80));
            }

            if (!double.IsNaN(_pdl))
            {
                int yPDL = (int)converter.GetChartY(_pdl);
                using (var pen = new Pen(Color.FromArgb(200, 255, 80, 80), 1) { DashStyle = DashStyle.Dash })
                    gr.DrawLine(pen, 0, yPDL, chartWidth, yPDL);
                DrawLabel(gr, "PDL", 4, yPDL + 2, Color.FromArgb(200, 255, 80, 80));
            }
        }

        // ── Order Block Rectangles ─────────────────────────────────────────────
        private void DrawOrderBlocks(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth)
        {
            var visible = _orderBlocks
                .Where(ob => ob.Status != ZoneStatus.Broken)
                .OrderBy(ob => ob.StartTime)
                .ToList();

            foreach (var ob in visible)
            {
                try
                {
                    int xStart = (int)converter.GetChartX(ob.StartTime);
                    int xEnd   = chartWidth;  // extend to right edge

                    int yTop    = (int)converter.GetChartY(ob.High);
                    int yBottom = (int)converter.GetChartY(ob.Low);
                    int yBodyT  = (int)converter.GetChartY(ob.BodyHigh);
                    int yBodyB  = (int)converter.GetChartY(ob.BodyLow);

                    int rectTop    = Math.Min(yTop, yBottom);
                    int rectHeight = Math.Abs(yBottom - yTop);
                    int bodyTop    = Math.Min(yBodyT, yBodyB);
                    int bodyHeight = Math.Abs(yBodyB - yBodyT);
                    int width      = xEnd - xStart;

                    if (width <= 0 || rectHeight <= 0) continue;

                    bool isHTF = ob.Timeframe != "5M";

                    // Wick zone (faded)
                    Color obColor = ob.IsBullish
                        ? (ob.Status == ZoneStatus.Mitigated ? Color.FromArgb(25, 50, 200, 100) : Color.FromArgb(35, 50, 230, 100))
                        : (ob.Status == ZoneStatus.Mitigated ? Color.FromArgb(25, 200, 50, 50)  : Color.FromArgb(35, 230, 80, 80));

                    using (var brush = new SolidBrush(obColor))
                        gr.FillRectangle(brush, xStart, rectTop, width, rectHeight);

                    // Body zone (stronger)
                    Color bodyColor = ob.IsBullish
                        ? Color.FromArgb(55, 50, 230, 100)
                        : Color.FromArgb(55, 230, 80, 80);

                    using (var brush = new SolidBrush(bodyColor))
                        gr.FillRectangle(brush, xStart, bodyTop, width, bodyHeight);

                    // Border
                    Color borderColor = ob.IsBullish ? Color.FromArgb(160, 50, 210, 100) : Color.FromArgb(160, 210, 70, 70);
                    float penWidth    = isHTF ? 2f : 1f;
                    DashStyle dash    = isHTF ? DashStyle.Dot : DashStyle.Solid;
                    using (var pen = new Pen(borderColor, penWidth) { DashStyle = dash })
                        gr.DrawRectangle(pen, xStart, rectTop, width, rectHeight);

                    // Label
                    string label = $"{(ob.IsBullish ? "OB+" : "OB-")} {ob.Timeframe}";
                    Color labelCol = ob.IsBullish ? Color.FromArgb(200, 80, 230, 120) : Color.FromArgb(200, 230, 100, 100);
                    DrawLabel(gr, label, xStart + 3, rectTop + 2, labelCol);
                }
                catch { /* skip individual OB drawing errors */ }
            }
        }

        // ── Fair Value Gap Rectangles ──────────────────────────────────────────
        private void DrawFairValueGaps(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth)
        {
            var visible = _fvgs.Where(f => f.Status == ZoneStatus.Active).ToList();

            foreach (var fvg in visible)
            {
                try
                {
                    int xStart = (int)converter.GetChartX(fvg.StartTime);
                    int xEnd   = chartWidth;

                    int yTop    = (int)converter.GetChartY(fvg.Top);
                    int yBottom = (int)converter.GetChartY(fvg.Bottom);
                    int yMid    = (int)converter.GetChartY(fvg.Midpoint);

                    int rectTop    = Math.Min(yTop, yBottom);
                    int rectHeight = Math.Abs(yBottom - yTop);
                    int width      = xEnd - xStart;

                    if (width <= 0 || rectHeight < 2) continue;

                    Color fillColor = fvg.IsBullish
                        ? Color.FromArgb(40, 50, 150, 255)     // Blue for bullish FVG
                        : Color.FromArgb(40, 255, 140, 0);     // Orange for bearish FVG

                    using (var brush = new SolidBrush(fillColor))
                        gr.FillRectangle(brush, xStart, rectTop, width, rectHeight);

                    Color borderColor = fvg.IsBullish
                        ? Color.FromArgb(120, 80, 160, 255)
                        : Color.FromArgb(120, 255, 165, 0);

                    using (var pen = new Pen(borderColor, 1))
                        gr.DrawRectangle(pen, xStart, rectTop, width, rectHeight);

                    // Consequent Encroachment (midpoint) dashed line
                    using (var pen = new Pen(borderColor, 1) { DashStyle = DashStyle.Dot })
                        gr.DrawLine(pen, xStart, yMid, xEnd, yMid);

                    // Label
                    string label = fvg.IsBullish ? "FVG+" : "FVG-";
                    Color labelCol = fvg.IsBullish ? Color.FromArgb(200, 100, 180, 255) : Color.FromArgb(200, 255, 165, 0);
                    DrawLabel(gr, label, xStart + 3, rectTop + 2, labelCol);
                }
                catch { }
            }
        }

        // ── Liquidity Sweep Markers ────────────────────────────────────────────
        private void DrawLiquiditySweeps(Graphics gr, IChartWindowCoordinatesConverter converter)
        {
            var recentSweeps = _liquiditySweeps
                .Where(ls => (DateTime.UtcNow - ls.Time).TotalHours <= 24)
                .ToList();

            foreach (var sweep in recentSweeps)
            {
                try
                {
                    int x = (int)converter.GetChartX(sweep.Time);
                    int y = (int)converter.GetChartY(sweep.Level);

                    Color col = sweep.IsBuySideSweep ? Color.FromArgb(220, 255, 80, 80) : Color.FromArgb(220, 80, 255, 140);

                    // Diamond marker at sweep level
                    var pts = new PointF[]
                    {
                        new PointF(x, y - 6),
                        new PointF(x + 5, y),
                        new PointF(x, y + 6),
                        new PointF(x - 5, y)
                    };

                    using (var brush = new SolidBrush(col))
                        gr.FillPolygon(brush, pts);

                    DrawLabel(gr, sweep.LevelLabel, x + 8, y - 7, col);
                }
                catch { }
            }
        }

        // ── Structure Event Labels (MSS / BOS) ────────────────────────────────
        private void DrawStructureEvents(Graphics gr, IChartWindowCoordinatesConverter converter)
        {
            var recent = _structureEvents
                .Where(se => (DateTime.UtcNow - se.Time).TotalHours <= 8)
                .ToList();

            foreach (var ev in recent)
            {
                try
                {
                    int x = (int)converter.GetChartX(ev.Time);
                    int y = (int)converter.GetChartY(ev.Level);

                    Color col = ev.Direction == SetupDirection.Long
                        ? Color.FromArgb(220, 80, 220, 120)
                        : Color.FromArgb(220, 220, 80, 80);

                    string label = $"{ev.Type}";

                    // Horizontal line at structure level
                    using (var pen = new Pen(col, 1) { DashStyle = DashStyle.DashDot })
                        gr.DrawLine(pen, x - 20, y, x + 60, y);

                    // Label box
                    using (var font = new Font("Arial", 7f, FontStyle.Bold))
                    {
                        SizeF sz = gr.MeasureString(label, font);
                        int lx = x + 2;
                        int ly = ev.Direction == SetupDirection.Long ? y - (int)sz.Height - 2 : y + 2;

                        using (var bg = new SolidBrush(Color.FromArgb(160, 20, 20, 30)))
                            gr.FillRectangle(bg, lx - 1, ly - 1, sz.Width + 2, sz.Height + 2);
                        using (var brush = new SolidBrush(col))
                            gr.DrawString(label, font, brush, lx, ly);
                    }
                }
                catch { }
            }
        }

        // ── Active Setup Lines (Entry / SL / TP) ──────────────────────────────
        private void DrawActiveSetups(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth)
        {
            foreach (var setup in _activeSetups.OrderByDescending(s => s.ConfluenceScore))
            {
                try
                {
                    bool isLong = setup.Direction == SetupDirection.Long;
                    Color entryCol = isLong ? Color.FromArgb(200, 80, 230, 100)  : Color.FromArgb(200, 230, 80, 80);
                    Color slCol    = Color.FromArgb(200, 230, 60, 60);
                    Color tpCol    = Color.FromArgb(200, 60, 200, 230);
                    Color tp2Col   = Color.FromArgb(160, 60, 160, 230);
                    Color bgCol    = setup.IsUpcomingSetup
                        ? Color.FromArgb(30, 180, 180, 50)
                        : Color.FromArgb(30, isLong ? 50 : 200, isLong ? 200 : 50, 80);

                    int yEntry = (int)converter.GetChartY(setup.EntryPrice);
                    int ySL    = (int)converter.GetChartY(setup.StopLoss);
                    int yTP    = (int)converter.GetChartY(setup.TakeProfit);
                    int yTP2   = (int)converter.GetChartY(setup.TakeProfit2);

                    // Entry zone background between entry and TP
                    int zoneTop = Math.Min(yEntry, yTP);
                    int zoneH   = Math.Abs(yTP - yEntry);
                    if (zoneH > 0)
                    {
                        using (var brush = new SolidBrush(bgCol))
                            gr.FillRectangle(brush, chartWidth - 200, zoneTop, 200, zoneH);
                    }

                    float dashW = setup.IsUpcomingSetup ? 1.5f : 2f;
                    DashStyle entryDash = setup.IsUpcomingSetup ? DashStyle.Dash : DashStyle.Solid;

                    // Entry line
                    using (var pen = new Pen(entryCol, dashW) { DashStyle = entryDash })
                        gr.DrawLine(pen, 0, yEntry, chartWidth, yEntry);
                    DrawLabel(gr, $"ENTRY {setup.EntryPrice:F1}", chartWidth - 120, yEntry - 14, entryCol);

                    // SL line
                    using (var pen = new Pen(slCol, 1.5f) { DashStyle = DashStyle.Dash })
                        gr.DrawLine(pen, 0, ySL, chartWidth, ySL);
                    DrawLabel(gr, $"SL {setup.StopLoss:F1}", chartWidth - 100, ySL + 2, slCol);

                    // TP1 line
                    using (var pen = new Pen(tpCol, 1.5f))
                        gr.DrawLine(pen, 0, yTP, chartWidth, yTP);
                    DrawLabel(gr, $"TP1 +{TargetTicks:F0}t", chartWidth - 100, yTP - 14, tpCol);

                    // TP2 line (dashed)
                    using (var pen = new Pen(tp2Col, 1f) { DashStyle = DashStyle.Dot })
                        gr.DrawLine(pen, 0, yTP2, chartWidth, yTP2);
                    DrawLabel(gr, $"TP2 +{TargetTicks2:F0}t", chartWidth - 100, yTP2 - 14, tp2Col);

                    // Score + direction badge (right side)
                    DrawSetupBadge(gr, setup, chartWidth - 200, yEntry);
                }
                catch { }
            }
        }

        private void DrawSetupBadge(Graphics gr, TradeSetup setup, int x, int yEntry)
        {
            bool isLong  = setup.Direction == SetupDirection.Long;
            string arrow = isLong ? "▲ LONG" : "▼ SHORT";
            string score = $"Score: {setup.ConfluenceScore}/{setup.MaxScore}";
            string state = setup.IsUpcomingSetup ? "DEVELOPING" : "SETUP";
            Color badgeCol = setup.IsUpcomingSetup
                ? Color.FromArgb(220, 200, 180, 0)
                : (isLong ? Color.FromArgb(220, 0, 200, 80) : Color.FromArgb(220, 200, 60, 60));

            using (var font = new Font("Consolas", 8f, FontStyle.Bold))
            using (var smallF = new Font("Arial", 7f))
            {
                int lineH = 13;
                int badgeW = 130;
                int badgeH = 60;
                int bx = x;
                int by = yEntry - badgeH / 2;

                // Background
                using (var bg = new SolidBrush(Color.FromArgb(185, 10, 15, 25)))
                    gr.FillRectangle(bg, bx, by, badgeW, badgeH);
                using (var border = new Pen(badgeCol, 1))
                    gr.DrawRectangle(border, bx, by, badgeW, badgeH);

                // Direction
                using (var b = new SolidBrush(badgeCol))
                {
                    gr.DrawString(arrow, font, b, bx + 4, by + 4);
                    gr.DrawString(state, smallF, b, bx + 4, by + 4 + lineH);
                    gr.DrawString(score, smallF, b, bx + 4, by + 4 + lineH * 2);
                }

                // Confluences (green dots)
                int cy = by + 4 + lineH * 3;
                string hitStr = string.Join(" ", setup.ConfluenceItems.Take(3).Select(c => "●"));
                string missStr = string.Join(" ", setup.MissingItems.Take(3).Select(c => "○"));
                using (var hb = new SolidBrush(Color.FromArgb(200, 80, 230, 120)))
                    gr.DrawString(hitStr, smallF, hb, bx + 4, cy);
                using (var mb = new SolidBrush(Color.FromArgb(140, 160, 160, 160)))
                    gr.DrawString(missStr, smallF, mb, bx + 4 + hitStr.Length * 6, cy);
            }
        }

        // ── Premium / Discount Zone Bands ─────────────────────────────────────
        private void DrawPremiumDiscountZones(Graphics gr, IChartWindowCoordinatesConverter converter, int chartWidth)
        {
            if (double.IsNaN(_rangeHigh) || double.IsNaN(_rangeLow)) return;
            double eq = GetEquilibrium();

            try
            {
                int yHigh = (int)converter.GetChartY(_rangeHigh);
                int yEQ   = (int)converter.GetChartY(eq);
                int yLow  = (int)converter.GetChartY(_rangeLow);

                // Premium zone (above EQ) — very faint red
                int premTop = Math.Min(yHigh, yEQ);
                int premH   = Math.Abs(yEQ - yHigh);
                if (premH > 0)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(10, 220, 80, 80)))
                        gr.FillRectangle(brush, 0, premTop, 80, premH);
                    DrawLabel(gr, "PREM", 2, premTop + 4, Color.FromArgb(80, 220, 100, 100));
                }

                // Discount zone (below EQ) — very faint green
                int discTop = Math.Min(yEQ, yLow);
                int discH   = Math.Abs(yLow - yEQ);
                if (discH > 0)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(10, 80, 220, 120)))
                        gr.FillRectangle(brush, 0, discTop, 80, discH);
                    DrawLabel(gr, "DISC", 2, discTop + discH - 14, Color.FromArgb(80, 100, 220, 120));
                }

                // Equilibrium line
                using (var pen = new Pen(Color.FromArgb(80, 200, 200, 200), 1) { DashStyle = DashStyle.Dot })
                    gr.DrawLine(pen, 0, yEQ, 80, yEQ);
                DrawLabel(gr, "EQ", 2, yEQ - 10, Color.FromArgb(80, 200, 200, 200));

                // OTE zones
                var (oteTop, oteBot) = GetOTEZone(SetupDirection.Long);
                if (!double.IsNaN(oteTop))
                {
                    int yOteTop = (int)converter.GetChartY(oteTop);
                    int yOteBot = (int)converter.GetChartY(oteBot);
                    int oteRectTop = Math.Min(yOteTop, yOteBot);
                    int oteH = Math.Abs(yOteBot - yOteTop);
                    if (oteH > 0)
                    {
                        using (var brush = new SolidBrush(Color.FromArgb(20, 100, 200, 255)))
                            gr.FillRectangle(brush, 0, oteRectTop, 80, oteH);
                        DrawLabel(gr, "OTE", 2, oteRectTop + 2, Color.FromArgb(100, 120, 200, 255));
                    }
                }
            }
            catch { }
        }

        // ── HUD Panel ──────────────────────────────────────────────────────────
        private void DrawHUD(Graphics gr, int chartWidth, int chartHeight)
        {
            try
            {
                DateTime utcNow  = DateTime.UtcNow;
                string session   = GetSessionLabel(utcNow);
                string kzLabel;
                bool   inKZ      = IsInKillZone(utcNow, out kzLabel);
                string kzStatus  = inKZ ? $"✓ {kzLabel}" : "— Off";
                string htfBias   = _htfBullish ? "▲ Bullish" : "▼ Bearish";
                string tradeCnt  = $"{_tradesExecToday}/{MaxTradesPerDay}";
                string pnlStr    = $"{(_dailyPnL >= 0 ? "+" : "")}{_dailyPnL:F0}";
                int    setups    = _activeSetups.Count(s => s.IsActive);
                int    devSetups = _activeSetups.Count(s => s.IsUpcomingSetup);

                string posStr    = "FLAT";
                if (_currentPosition != null && _currentPosition.Quantity != 0)
                    posStr = _currentPosition.Quantity > 0 ? "LONG" : "SHORT";

                // Lines to display
                var lines = new (string label, string value, Color col)[]
                {
                    ("SESSION",   session,                  Color.FromArgb(200, 200, 200, 200)),
                    ("KILL ZONE", kzStatus,                 inKZ ? Color.FromArgb(200, 80, 230, 120) : Color.FromArgb(160, 160, 160, 160)),
                    ("1H BIAS",   htfBias,                  _htfBullish ? Color.FromArgb(200, 80, 230, 120) : Color.FromArgb(200, 230, 80, 80)),
                    ("SETUPS",    $"{setups} active",       Color.FromArgb(200, 200, 200, 80)),
                    ("DEVELOP",   $"{devSetups} forming",   Color.FromArgb(160, 180, 180, 100)),
                    ("TRADES",    tradeCnt,                  Color.FromArgb(200, 180, 180, 255)),
                    ("DAY P&L",   pnlStr,                   _dailyPnL >= 0 ? Color.FromArgb(200, 80, 220, 120) : Color.FromArgb(200, 220, 80, 80)),
                    ("POSITION",  posStr,                   posStr == "FLAT" ? Color.FromArgb(160, 160, 160, 160) : Color.FromArgb(200, 255, 200, 50)),
                    ("MODE",      EnableTrading ? "AUTO" : "VISUAL", EnableTrading ? Color.FromArgb(200, 255, 165, 0) : Color.FromArgb(160, 160, 200, 160)),
                };

                int panelX = chartWidth - 200;
                int panelY = 10;
                int lineH  = 16;
                int panelW = 190;
                int panelH = lines.Length * lineH + 20;

                // Background panel
                using (var bg = new SolidBrush(Color.FromArgb(200, 12, 14, 22)))
                    gr.FillRectangle(bg, panelX, panelY, panelW, panelH);
                using (var border = new Pen(Color.FromArgb(120, 80, 140, 200), 1))
                    gr.DrawRectangle(border, panelX, panelY, panelW, panelH);

                // Title
                using (var titleFont = new Font("Consolas", 8f, FontStyle.Bold))
                using (var titleBrush = new SolidBrush(Color.FromArgb(200, 100, 180, 255)))
                    gr.DrawString("◈ 100 TICKS ICT", titleFont, titleBrush, panelX + 6, panelY + 4);

                // Separator
                using (var sep = new Pen(Color.FromArgb(60, 80, 80, 120), 1))
                    gr.DrawLine(sep, panelX + 4, panelY + 17, panelX + panelW - 4, panelY + 17);

                // Each line
                for (int i = 0; i < lines.Length; i++)
                {
                    int ly = panelY + 20 + i * lineH;
                    using (var keyBrush = new SolidBrush(Color.FromArgb(120, 160, 160, 160)))
                    using (var valBrush = new SolidBrush(lines[i].col))
                    using (var kf = _smallFont ?? new Font("Arial", 7f))
                    using (var vf = _labelFont ?? new Font("Arial", 8f, FontStyle.Bold))
                    {
                        gr.DrawString(lines[i].label + ":", kf, keyBrush, panelX + 6, ly + 1);
                        gr.DrawString(lines[i].value, vf, valBrush, panelX + 78, ly);
                    }
                }

                // Active setup preview (below HUD)
                var topSetup = _activeSetups.Where(s => s.IsActive).OrderByDescending(s => s.ConfluenceScore).FirstOrDefault();
                if (topSetup != null)
                {
                    int previewY = panelY + panelH + 8;
                    string dir   = topSetup.Direction == SetupDirection.Long ? "▲ LONG" : "▼ SHORT";
                    Color dc     = topSetup.Direction == SetupDirection.Long
                        ? Color.FromArgb(220, 80, 230, 100) : Color.FromArgb(220, 230, 80, 80);

                    using (var bg = new SolidBrush(Color.FromArgb(180, 12, 14, 22)))
                        gr.FillRectangle(bg, panelX, previewY, panelW, 54);
                    using (var border = new Pen(dc, 1))
                        gr.DrawRectangle(border, panelX, previewY, panelW, 54);
                    using (var f = new Font("Consolas", 8f, FontStyle.Bold))
                    using (var b = new SolidBrush(dc))
                    {
                        gr.DrawString($"{dir}  Score {topSetup.ConfluenceScore}/8", f, b, panelX + 6, previewY + 4);
                        using (var sf = new Font("Arial", 7f))
                        using (var sb2 = new SolidBrush(Color.FromArgb(180, 200, 200, 200)))
                        {
                            gr.DrawString($"Entry: {topSetup.EntryPrice:F1}", sf, sb2, panelX + 6, previewY + 18);
                            gr.DrawString($"SL:    {topSetup.StopLoss:F1}", sf, sb2, panelX + 6, previewY + 30);
                            gr.DrawString($"TP1:   {topSetup.TakeProfit:F1}", sf, sb2, panelX + 6, previewY + 42);
                        }
                    }
                }

                // Upcoming setup preview
                var nextSetup = _activeSetups.Where(s => s.IsUpcomingSetup).OrderByDescending(s => s.ConfluenceScore).FirstOrDefault();
                if (nextSetup != null)
                {
                    int nextY = panelY + panelH + (topSetup != null ? 70 : 8);
                    using (var bg = new SolidBrush(Color.FromArgb(160, 12, 14, 22)))
                        gr.FillRectangle(bg, panelX, nextY, panelW, 42);
                    using (var border = new Pen(Color.FromArgb(150, 200, 200, 50), 1) { DashStyle = DashStyle.Dash })
                        gr.DrawRectangle(border, panelX, nextY, panelW, 42);
                    using (var f = new Font("Consolas", 7f, FontStyle.Bold))
                    using (var b = new SolidBrush(Color.FromArgb(200, 200, 180, 50)))
                    {
                        gr.DrawString($"⧖ DEVELOPING: {(nextSetup.Direction == SetupDirection.Long ? "LONG" : "SHORT")} ({nextSetup.ConfluenceScore}/8)", f, b, panelX + 4, nextY + 4);
                        using (var sf = new Font("Arial", 7f))
                        using (var mb = new SolidBrush(Color.FromArgb(160, 180, 180, 180)))
                        {
                            string needs = string.Join(", ", nextSetup.MissingItems.Take(2));
                            gr.DrawString($"Needs: {needs}", sf, mb, panelX + 4, nextY + 18);
                        }
                    }
                }
            }
            catch { }
        }

        // ── Utility: Draw Text Label ───────────────────────────────────────────
        private void DrawLabel(Graphics gr, string text, int x, int y, Color col)
        {
            try
            {
                using (var font = new Font("Arial", 7.5f, FontStyle.Bold))
                using (var brush = new SolidBrush(col))
                    gr.DrawString(text, font, brush, x, y);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // DISPLAY PROPERTIES (Inspector panel)
        // ══════════════════════════════════════════════════════════════════════
        public string CurrentSession       => GetSessionLabel(DateTime.UtcNow);
        public bool   InKillZone           => IsInKillZone(DateTime.UtcNow, out _);
        public string HTFBias              => _htfBullish ? "Bullish" : "Bearish";
        public int    ActiveSetups         => _activeSetups.Count(s => s.IsActive);
        public int    DevelopingSetups     => _activeSetups.Count(s => s.IsUpcomingSetup);
        public int    TradesExecToday      => _tradesExecToday;
        public double DailyPnL            => _dailyPnL;
        public double PDH                  => _pdh;
        public double PDL                  => _pdl;
        public int    ActiveOrderBlocks    => _orderBlocks.Count(o => o.Status == ZoneStatus.Active);
        public int    ActiveFVGs           => _fvgs.Count(f => f.Status == ZoneStatus.Active);
    }
}
