# 100 Ticks Trade Indicator ICT

A professional-grade Quantower technical indicator implementing advanced Inner Circle Trader (ICT) concepts on a 5-minute timeframe. Designed for CME Micro Gold Futures (MGC), it targets a minimum of 100 ticks profit using multi-timeframe analysis and high-probability confluence scoring.

## Core Features

- **Multi-Timeframe Context Engine**: Analyzes 1-Hour structure for directional bias and 15-Minute structure for key levels, while executing on the 5-Minute chart.
- **Dynamic Order Blocks (OB)**: Detects bullish (`OB+`) and bearish (`OB-`) order blocks on both 5M and 15M timeframes. Draws them as colored rectangles extending to the right until mitigated or broken.
- **Fair Value Gaps (FVG)**: Highlights price imbalances with semi-transparent bands. Draws the Consequent Encroachment (50% midpoint) line.
- **Market Structure Shift (MSS) & Break of Structure (BOS)**: Tracks swing highs and lows to label structural trend shifts (MSS) and continuations (BOS) dynamically.
- **Liquidity Sweep Detection**: Tracks Previous Day High/Low (PDH/PDL), previous session levels, and local swing points to detect and mark stop hunts.
- **ICT Session Kill Zones**: Automatically highlights London (2-5 AM EST), New York (7-10 AM EST), and London Close (11 AM-1 PM EST) sessions with custom colored background panels.
- **Premium/Discount & OTE Zones**: Divides current range using Fibonacci levels to identify equilibrium, premium/discount zones, and the Optimal Trade Entry (61.8%–79%) sweet spot.
- **Confluence Setup Engine**: Scores setups from 0 to 8 based on confluences (Kill Zone, HTF Bias, discount/premium, liquidity sweep, OB, FVG, MSS/BOS, OTE). Requires a minimum score of 5/7 (configurable) to trigger.
- **Upcoming Setup Prediction**: Shows a "DEVELOPING" dashboard label when criteria are partially met, indicating what conditions are missing.
- **Visual Chart HUD**: Display panel containing current session, 1H bias, active setups, daily trade limits, daily P&L, position status, and details of the current trade setup.
- **Built-in Risk Management**: Enforces max 4 trades per day, fixed lot size, $50 max risk per trade (SL), and $100 daily realized loss limit. Supports both automatic trading and visual-only alert modes.

## Requirements

- Quantower Trading Platform
- .NET 10.0 SDK (Windows Desktop)
- CME data feed with Gold / Micro Gold Futures (MGC)

## Installation & Compilation

1. Open the solution in Visual Studio or build via CLI.
2. Build the project using:
   ```bash
   dotnet build "100 Ticks Trade Indicator ICT.csproj" --configuration Release
   ```
3. The build output will automatically copy the compiled DLL (`_100_Ticks_Trade_Indicator_ICT.dll`) to your Quantower script directory:
   `C:\Quantower\Settings\Scripts\Indicators\100 Ticks Trade Indicator ICT\`

## Input Parameters

- **Account Name Filter**: The account name or substring to resolve for trading (e.g. "Lucid").
- **Enable Auto-Trading**: If `true`, the indicator automatically submits market orders upon valid confluence setup trigger. Default is `false` (visual setup lines only).
- **Target Ticks (TP1)**: Profit target ticks for primary trade exit (default: `100` ticks).
- **Target Ticks (TP2)**: Profit target ticks for secondary runner exit (default: `150` ticks).
- **Max Risk Ticks (SL)**: Stop loss protection in ticks (default: `50` ticks).
- **Daily Loss Limit ($)**: Daily max realized loss limit before trading is deactivated (default: `$100`).
- **Max Trades Per Day**: Daily trade count limit (default: `4`).
- **Min Confluence Score**: Minimum setup score (0-8) required to trigger entry (default: `5`).
- **Show/Hide Settings**: Toggles visibility of Order Blocks, FVGs, Kill Zones, PDH/PDL levels, HTF zones, and the HUD panel.
