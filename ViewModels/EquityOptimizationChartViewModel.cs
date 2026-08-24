// ViewModels/EquityOptimizationChartViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MoneyGenerator_v5.Models;
using ScottPlot;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class EquityOptimizationChartViewModel : ObservableObject, IDisposable
    {
        private string _tickerName;
        private string _timeFrame;
        private DateTime _currentData;

        private readonly OptimizationResult _result;
        private readonly WpfPlot _plot;
        private bool _disposed = false;

        // Базовые свойства
        [ObservableProperty]
        private string _parametersSummary;

        [ObservableProperty]
        private decimal _initialCapital;

        [ObservableProperty]
        private decimal _finalCapital;

        [ObservableProperty]
        private decimal _totalProfit;

        [ObservableProperty]
        private string _totalDuration;

        [ObservableProperty]
        private double _maxDrawdown;

        [ObservableProperty]
        private Brush _profitColor;

        [ObservableProperty]
        private Brush _finalCapitalColor;

        [ObservableProperty]
        private int _selectedDays = 0;

        // ✅ НОВЫЕ СВОЙСТВА ДЛЯ ИНФОРМАЦИОННОЙ ПАНЕЛИ
        [ObservableProperty]
        private string _optimizationPeriodsFromTill;

        [ObservableProperty]
        private string _totalTradesInfo;

        [ObservableProperty]
        private string _winRateInfo;

        [ObservableProperty]
        private string _profitFactorInfo;

        [ObservableProperty]
        private string _sharpeRatioInfo;

        [ObservableProperty]
        private string _recoveryFactorInfo;

        [ObservableProperty]
        private string _overallRating;

        [ObservableProperty]
        private Brush _ratingColor;

        [ObservableProperty]
        private string _ratingStars;

        //[ObservableProperty]
        //private string _ratingDescription;


        [ObservableProperty]
        private string _ratingDescriptionStrengths;

        [ObservableProperty]
        private string _ratingDescriptionweaknesses;


        // ✅ НОВЫЕ СВОЙСТВА ДЛЯ TOOLTIP
        [ObservableProperty]
        private string _toolTipInitialCapital;

        [ObservableProperty]
        private string _toolTipFinalCapital;

        [ObservableProperty]
        private string _toolTipTotalProfit;

        [ObservableProperty]
        private string _toolTipMaxDrawdown;

        [ObservableProperty]
        private string _toolTipWinRate;

        [ObservableProperty]
        private string _toolTipProfitFactor;

        [ObservableProperty]
        private string _toolTipSharpeRatio;

        [ObservableProperty]
        private string _toolTipRecoveryFactor;

        [ObservableProperty]
        private string _toolTipTotalTrades;

        [ObservableProperty]
        private string _toolTipOptimizationPeriod;

        [ObservableProperty]
        private string _toolTipOverallRating;





        public IRelayCommand SetFullHistoryCommand { get; }
        public IRelayCommand RefreshCommand { get; }
        public IRelayCommand ExportCommand { get; }

        public EquityOptimizationChartViewModel(OptimizationResult result, WpfPlot plot)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _tickerName = result.Ticker;
            _timeFrame = result.TimeFrame;
            _currentData = result.EndDate;

            SetFullHistoryCommand = new RelayCommand(SetFullHistory);
            RefreshCommand = new RelayCommand(Refresh);
            ExportCommand = new RelayCommand(Export);

            InitializeData();
            InitializeRating();
            InitializeToolTips();

            // Настраиваем график
            ConfigurePlot();
            PlotEquity();
        }

        private void InitializeData()
        {
            if (_result.EquityHistory == null || _result.EquityHistory.Count == 0)
            {
                ParametersSummary = "Нет данных эквити";
                return;
            }

            // Параметры
            ParametersSummary = string.Join(" | ", _result.Parameters.Select(p => $"{p.Key}={p.Value:F2}"));

            // Капитал
            InitialCapital = _result.EquityHistory.FirstOrDefault();
            FinalCapital = _result.EquityHistory.LastOrDefault();
            TotalProfit = FinalCapital - InitialCapital;

            ProfitColor = TotalProfit >= 0 ? new SolidColorBrush(System.Windows.Media.Colors.Green) : new SolidColorBrush(System.Windows.Media.Colors.Red);
            FinalCapitalColor = FinalCapital >= InitialCapital ? new SolidColorBrush(System.Windows.Media.Colors.Green) : new SolidColorBrush(System.Windows.Media.Colors.Red);

            // Просадка
            decimal peak = InitialCapital;
            double maxDrawdown = 0;
            foreach (var equity in _result.EquityHistory)
            {
                if (equity > peak)
                    peak = equity;
                decimal drawdown = peak > 0 ? (peak - equity) / peak * 100 : 0;
                if ((double)drawdown > maxDrawdown)
                    maxDrawdown = (double)drawdown;
            }
            MaxDrawdown = maxDrawdown;

            // Длительность
            if (_result.EquityDates != null && _result.EquityDates.Count >= 2)
            {
                var duration = _result.EquityDates.Last() - _result.EquityDates.First();
                TotalDuration = FormatDuration(duration);
            }
            else
            {
                TotalDuration = "Нет данных";
            }

            // ✅ ПЕРИОД ОПТИМИЗАЦИИ
            if (_result.StartDate != DateTime.MinValue && _result.EndDate != DateTime.MinValue)
            {
                OptimizationPeriodsFromTill = $"{_result.StartDate:dd.MM.yyyy HH:mm} — {_result.EndDate:dd.MM.yyyy HH:mm}";
            }
            else
            {
                OptimizationPeriodsFromTill = "Нет данных";
            }

            // ✅ ИНФОРМАЦИЯ О СДЕЛКАХ
            TotalTradesInfo = _result.TotalTrades > 0
                ? $"{_result.TotalTrades} сделок ({_result.WinningTrades} выигрышных, {_result.LosingTrades} проигрышных)"
                : "Нет сделок";

            WinRateInfo = _result.TotalTrades > 0
                ? $"{_result.WinRate:F1}%"
                : "Н/Д";

            ProfitFactorInfo = _result.TotalTrades > 0
                ? $"{_result.ProfitFactor:F2}"
                : "Н/Д";

            SharpeRatioInfo = _result.SharpeRatio != 0
                ? $"{_result.SharpeRatio:F2}"
                : "Н/Д";

            RecoveryFactorInfo = _result.RecoveryFactor != 0
                ? $"{_result.RecoveryFactor:F2}"
                : "Н/Д";
        }

        /// <summary>
        /// Инициализирует подробные ToolTip для всех элементов
        /// </summary>
        private void InitializeToolTips()
        {
            // Начальный капитал
            ToolTipInitialCapital =
                "💵 Начальный капитал\n" +
                "═══════════════════════\n" +
                $"Сумма на счете в начале торговли: {InitialCapital:F2} ₽\n\n" +
                "📌 Это стартовая точка для расчета P&L.\n" +
                "Используется как база для сравнения результатов.";

            // Конечный капитал
            ToolTipFinalCapital =
                "💵 Конечный капитал\n" +
                "═══════════════════════\n" +
                $"Сумма на счете в конце периода: {FinalCapital:F2} ₽\n\n" +
                "📌 Итоговый результат торговли.\n" +
                $"{(FinalCapital >= InitialCapital ? "✅ Прибыль" : "❌ Убыток")} за весь период.";

            // P&L
            ToolTipTotalProfit =
                "📊 Прибыль/Убыток (P&L)\n" +
                "═══════════════════════\n" +
                $"Общий финансовый результат: {TotalProfit:F2} ₽\n\n" +
                "📌 Рассчитывается как:\n" +
                "   P&L = Конечный капитал - Начальный капитал\n\n" +
                "✅ Положительное значение = прибыль\n" +
                "❌ Отрицательное значение = убыток";

            // Максимальная просадка
            ToolTipMaxDrawdown =
                "📉 Максимальная просадка\n" +
                "═══════════════════════\n" +
                $"Максимальная просадка: {MaxDrawdown:F1}%\n\n" +
                "📌 Показывает максимальное падение капитала\n" +
                "от пикового значения в процентах.\n\n" +
                "⚠️ Важный показатель риска:\n" +
                $"{(MaxDrawdown < 5 ? "✅ Низкий риск" : MaxDrawdown < 15 ? "🟡 Средний риск" : "🔴 Высокий риск")}";

            // Win Rate
            ToolTipWinRate =
                "🎯 Win Rate (процент выигрышных сделок)\n" +
                "══════════════════════════════════════\n" +
                $"Процент выигрышных сделок: {_result.WinRate:F1}%\n\n" +
                "📌 Рассчитывается как:\n" +
                "   Win Rate = (Выигрышные / Всего сделок) × 100%\n\n" +
                $"📊 Сделок: {_result.TotalTrades}\n" +
                $"   ✅ Выигрышных: {_result.WinningTrades}\n" +
                $"   ❌ Проигрышных: {_result.LosingTrades}\n\n" +
                $"{(_result.WinRate >= 55 ? "✅ Хороший показатель" : "⚠️ Требуется улучшение")}";

            // Profit Factor
            ToolTipProfitFactor =
                "📈 Profit Factor (Фактор прибыли)\n" +
                "══════════════════════════════════\n" +
                $"Отношение прибыли к убыткам: {_result.ProfitFactor:F2}\n\n" +
                "📌 Рассчитывается как:\n" +
                "   Profit Factor = Сумма прибыли / Сумма убытков\n\n" +
                "📊 Интерпретация:\n" +
                $"   • > 2.0  = {(_result.ProfitFactor >= 2.0m ? "✅" : "⬜")} Отличный результат\n" +
                $"   • 1.5-2.0 = {(_result.ProfitFactor >= 1.5m && _result.ProfitFactor < 2.0m ? "✅" : "⬜")} Хороший результат\n" +
                $"   • 1.2-1.5 = {(_result.ProfitFactor >= 1.2m && _result.ProfitFactor < 1.5m ? "🟡" : "⬜")} Удовлетворительный\n" +
                $"   • < 1.2  = {(_result.ProfitFactor < 1.2m && _result.ProfitFactor > 0 ? "❌" : "⬜")} Требует улучшения";

            // Sharpe Ratio
            ToolTipSharpeRatio =
                "📉 Sharpe Ratio (Коэффициент Шарпа)\n" +
                "════════════════════════════════════\n" +
                $"Коэффициент Шарпа: {_result.SharpeRatio:F2}\n\n" +
                "📌 Показывает доходность на единицу риска.\n" +
                "Чем выше значение, тем лучше соотношение риск/прибыль.\n\n" +
                "📊 Интерпретация:\n" +
                $"   • > 1.0  = {(_result.SharpeRatio >= 1.0m ? "✅" : "⬜")} Отличный результат\n" +
                $"   • 0.5-1.0 = {(_result.SharpeRatio >= 0.5m && _result.SharpeRatio < 1.0m ? "🟡" : "⬜")} Хороший результат\n" +
                $"   • 0-0.5   = {(_result.SharpeRatio > 0 && _result.SharpeRatio < 0.5m ? "⚠️" : "⬜")} Удовлетворительный\n" +
                $"   • < 0    = {(_result.SharpeRatio < 0 ? "❌" : "⬜")} Отрицательный результат";

            // Recovery Factor
            ToolTipRecoveryFactor =
                "🔄 Recovery Factor (Фактор восстановления)\n" +
                "═══════════════════════════════════════\n" +
                $"Фактор восстановления: {_result.RecoveryFactor:F2}\n\n" +
                "📌 Показывает, насколько быстро капитал\n" +
                "восстанавливается после просадок.\n\n" +
                "📊 Рассчитывается как:\n" +
                "   Recovery Factor = P&L / Макс.просадка\n\n" +
                $"{(RecoveryFactorInfo != "Н/Д" && _result.RecoveryFactor > 1.0m ? "✅ Хорошее восстановление" : "⚠️ Медленное восстановление")}";

            // Total Trades
            ToolTipTotalTrades =
                "📊 Статистика сделок\n" +
                "═══════════════════════\n" +
                $"Всего сделок: {_result.TotalTrades}\n" +
                $"✅ Выигрышных: {_result.WinningTrades}\n" +
                $"❌ Проигрышных: {_result.LosingTrades}\n\n" +
                "📌 Чем больше сделок, тем статистически\n" +
                "значимее результаты стратегии.\n\n" +
                $"{(RecoveryFactorInfo != "Н/Д" && _result.TotalTrades >= 30 ? "✅ Достаточная статистика" : "⚠️ Мало сделок для надежных выводов")}";

            // Optimization Period
            ToolTipOptimizationPeriod =
                "📅 Период оптимизации\n" +
                "═══════════════════════\n" +
                $"Период: {OptimizationPeriodsFromTill}\n\n" +
                "📌 Исторический период, на котором\n" +
                "проводилась оптимизация параметров.\n\n" +
                "⚠️ Важно: Результаты могут не сохраняться\n" +
                "при торговле на других временных интервалах.";

            // Overall Rating
            ToolTipOverallRating =
                "⭐ Общая оценка стратегии\n" +
                "═══════════════════════\n" +
                $"Рейтинг: {OverallRating}\n" +
                $"Звезды: {RatingStars}\n\n" +
                "📌 Комплексная оценка основана на:\n" +
                "   • Прибыльности (P&L)\n" +
                "   • Проценте выигрышных сделок\n" +
                "   • Факторе прибыли\n" +
                "   • Максимальной просадке\n" +
                "   • Коэффициенте Шарпа\n" +
                "   • Количестве сделок\n\n" +
                $"{(OverallRating.Contains("Отлично") ? "✅ Стратегия готова к использованию!" :
                  OverallRating.Contains("Хорошо") ? "🟡 Стратегия требует доработки." :
                  "🔴 Стратегия требует пересмотра.")}";
        }








        /// <summary>
        /// Вычисляет общую оценку результатов оптимизации
        /// </summary>
        private void InitializeRating()
        {
            if (_result.TotalTrades == 0 || _result.NetProfit == 0)
            {
                OverallRating = "Нет данных для оценки";
                RatingColor = new SolidColorBrush(System.Windows.Media.Colors.Gray);
                RatingStars = "☆☆☆☆☆";
                //RatingDescription = "Недостаточно данных для оценки";
                RatingDescriptionStrengths = "Недостаточно данных для оценки";
                RatingDescriptionweaknesses = "Недостаточно данных для оценки";
                return;
            }

            // ✅ СИСТЕМА ОЦЕНКИ ПО МНОЖЕСТВУ КРИТЕРИЕВ
            int score = 0;
            int maxScore = 0;
            List<string> strengths = new List<string>();
            List<string> weaknesses = new List<string>();

            // 1. Прибыльность (макс. 30 баллов)
            maxScore += 30;
            if (_result.NetProfit > 0)
            {
                if (_result.NetProfit > 10000)
                {
                    score += 30;
                    strengths.Add("Высокая прибыль (>10 000 ₽)");
                }
                else if (_result.NetProfit > 5000)
                {
                    score += 25;
                    strengths.Add("Хорошая прибыль (>5 000 ₽)");
                }
                else if (_result.NetProfit > 1000)
                {
                    score += 20;
                    strengths.Add("Умеренная прибыль (>1 000 ₽)");
                }
                else
                {
                    score += 10;
                    strengths.Add("Небольшая прибыль");
                }
            }
            else
            {
                weaknesses.Add($"Убыток: {_result.NetProfit:F2} ₽");
            }

            // 2. Win Rate (макс. 20 баллов)
            maxScore += 20;
            if (_result.WinRate >= 70)
            {
                score += 20;
                strengths.Add($"Отличный Win Rate: {_result.WinRate:F1}%");
            }
            else if (_result.WinRate >= 55)
            {
                score += 15;
                strengths.Add($"Хороший Win Rate: {_result.WinRate:F1}%");
            }
            else if (_result.WinRate >= 40)
            {
                score += 10;
            }
            else
            {
                weaknesses.Add($"Низкий Win Rate: {_result.WinRate:F1}%");
            }

            // 3. Profit Factor (макс. 20 баллов)
            maxScore += 20;
            if (_result.ProfitFactor >= 2.0m)
            {
                score += 20;
                strengths.Add($"Отличный Profit Factor: {_result.ProfitFactor:F2}");
            }
            else if (_result.ProfitFactor >= 1.5m)
            {
                score += 15;
                strengths.Add($"Хороший Profit Factor: {_result.ProfitFactor:F2}");
            }
            else if (_result.ProfitFactor >= 1.2m)
            {
                score += 10;
            }
            else
            {
                weaknesses.Add($"Низкий Profit Factor: {_result.ProfitFactor:F2}");
            }

            // 4. Max Drawdown (макс. 15 баллов)
            maxScore += 15;
            if (_result.MaxDrawdown < 5)
            {
                score += 15;
                strengths.Add($"Минимальная просадка: {_result.MaxDrawdown:F1}%");
            }
            else if (_result.MaxDrawdown < 15)
            {
                score += 10;
                strengths.Add($"Умеренная просадка: {_result.MaxDrawdown:F1}%");
            }
            else if (_result.MaxDrawdown < 30)
            {
                score += 5;
            }
            else
            {
                weaknesses.Add($"Высокая просадка: {_result.MaxDrawdown:F1}%");
            }

            // 5. Sharpe Ratio (макс. 10 баллов)
            maxScore += 10;
            if (_result.SharpeRatio >= 1.0m)
            {
                score += 10;
                strengths.Add($"Отличный Sharpe: {_result.SharpeRatio:F2}");
            }
            else if (_result.SharpeRatio >= 0.5m)
            {
                score += 6;
                strengths.Add($"Хороший Sharpe: {_result.SharpeRatio:F2}");
            }
            else if (_result.SharpeRatio > 0)
            {
                score += 3;
            }
            else
            {
                weaknesses.Add($"Отрицательный Sharpe: {_result.SharpeRatio:F2}");
            }

            // 6. Количество сделок (макс. 5 баллов)
            maxScore += 5;
            if (_result.TotalTrades >= 30)
            {
                score += 5;
                strengths.Add($"Достаточная статистика: {_result.TotalTrades} сделок");
            }
            else if (_result.TotalTrades >= 10)
            {
                score += 3;
            }
            else
            {
                weaknesses.Add($"Мало сделок: {_result.TotalTrades}");
            }

            // ✅ ВЫЧИСЛЯЕМ ПРОЦЕНТ ОЦЕНКИ
            double percent = maxScore > 0 ? (double)score / maxScore * 100 : 0;

            // ✅ ОПРЕДЕЛЯЕМ РЕЙТИНГ (используем System.Windows.Media.Colors)
            string ratingText;
            System.Windows.Media.Color color;
            string stars;
            string description;

            if (percent >= 85)
            {
                ratingText = "Отлично";
                color = System.Windows.Media.Colors.Green;
                stars = "⭐⭐⭐⭐⭐";
                description = "Отличные результаты! Стратегия показывает стабильную прибыль с хорошим соотношением риск/прибыль.";
            }
            else if (percent >= 70)
            {
                ratingText = "Хорошо";
                color = System.Windows.Media.Colors.LightGreen;
                stars = "⭐⭐⭐⭐";
                description = "Хорошие результаты. Стратегия работает эффективно, но есть куда расти.";
            }
            else if (percent >= 55)
            {
                ratingText = "Средне";
                color = System.Windows.Media.Colors.Gold;
                stars = "⭐⭐⭐";
                description = "Удовлетворительные результаты. Рекомендуется доработать параметры.";
            }
            else if (percent >= 40)
            {
                ratingText = "Ниже среднего";
                color = System.Windows.Media.Colors.Orange;
                stars = "⭐⭐";
                description = "Результаты ниже ожидаемых. Требуется пересмотр стратегии.";
            }
            else
            {
                ratingText = "Неудовлетворительно";
                color = System.Windows.Media.Colors.Red;
                stars = "⭐";
                description = "Результаты неудовлетворительные. Рекомендуется полностью пересмотреть стратегию.";
            }

            OverallRating = $"{ratingText} ({percent:F0}%)";
            RatingColor = new SolidColorBrush(color);
            RatingStars = stars;
            //RatingDescription = description;
            RatingDescriptionStrengths = description;
            RatingDescriptionweaknesses = description;

            // ✅ ДОБАВЛЯЕМ ДЕТАЛЬНЫЙ АНАЛИЗ В ОПИСАНИЕ
            if (strengths.Any())
            {
                var detailsStrengths = new List<string>();

                {
                    detailsStrengths.Add("✅ Сильные стороны:");
                    detailsStrengths.AddRange(strengths.Select(s => $"   • {s}"));
                }

                RatingDescriptionStrengths = string.Join("  \n   ", detailsStrengths);


               
            }

            // ✅ ДОБАВЛЯЕМ ДЕТАЛЬНЫЙ АНАЛИЗ В ОПИСАНИЕ
            if (weaknesses.Any())
            {

                var detailsweaknesses = new List<string>();

                if (weaknesses.Any())
                {
                    if (detailsweaknesses.Any()) detailsweaknesses.Add("");
                    detailsweaknesses.Add("⚠️ Слабые стороны:");
                    detailsweaknesses.AddRange(weaknesses.Select(w => $"   • {w}"));
                }

                RatingDescriptionweaknesses = string.Join("  \n   ", detailsweaknesses);
            }

        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays < 1)
                return $"{duration.Hours}ч {duration.Minutes}м";
            else if (duration.TotalDays < 30)
                return $"{duration.Days}д {duration.Hours}ч";
            else if (duration.TotalDays < 365)
                return $"{duration.Days / 30}м {duration.Days % 30}д";
            else
                return $"{duration.Days / 365}г {duration.Days % 365 / 30}м";
        }

        private void ConfigurePlot()
        {
            _plot.Plot.Title($"График эквити оптимизации");
            _plot.Plot.YLabel("Баланс (₽)");
            _plot.Plot.XLabel("Дата");
            _plot.Plot.ShowLegend();

            // Настройка сетки
            _plot.Plot.Grid.MajorLineColor = ScottPlot.Colors.Gray.WithAlpha(0.2);
            _plot.Plot.Grid.MajorLineWidth = 1;

            _plot.Refresh();
        }

        private void PlotEquity()
        {
            if (_result.EquityHistory == null || _result.EquityHistory.Count < 2)
                return;

            try
            {
                _plot.Plot.Clear();

                // Подготовка данных
                var data = _result.EquityHistory.ToArray();
                var dates = _result.EquityDates?.ToArray();

                // Используем индексы как X значения
                var xs = Enumerable.Range(0, data.Length).Select(i => (double)i).ToArray();
                var ys = data.Select(d => (double)d).ToArray();

                // Сохраняем даты для подписей
                var dateStrings = dates?.Select(d => d.ToString("dd.MM HH:mm")).ToArray();

                // Основная линия эквити
                var scatter = _plot.Plot.Add.Scatter(xs, ys);
                scatter.Label = "Эквити";
                scatter.Color = ScottPlot.Color.FromHex("#3498DB");
                scatter.LineWidth = 2;
                scatter.MarkerSize = 0;

                // Добавляем начальный уровень
                var initialLine = _plot.Plot.Add.HorizontalLine((double)InitialCapital);
                initialLine.LabelText = $"Нач. капитал: {InitialCapital:F2}";
                initialLine.Color = ScottPlot.Color.FromHex("#E74C3C");
                initialLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Gray.WithAlpha(0.5),
                    Width = 1,
                    Pattern = LinePattern.Dashed
                };

                // Добавляем текущий уровень (финальный капитал)
                var finalLine = _plot.Plot.Add.HorizontalLine((double)FinalCapital);
                finalLine.LabelText = $"Текущий капитал: {FinalCapital:F2}";
                finalLine.Color = ScottPlot.Color.FromHex("#2ECC71");
                finalLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Green.WithAlpha(0.7),
                    Width = 2,
                    Pattern = LinePattern.Dashed
                };

                // Настройка оси X с датами
                if (dateStrings != null && dateStrings.Length > 0)
                {
                    int tickCount = Math.Min(10, dateStrings.Length);
                    int tickStep = Math.Max(1, dateStrings.Length / tickCount);

                    var tickPositions = new List<double>();
                    var tickLabels = new List<string>();

                    for (int i = 0; i < dateStrings.Length; i += tickStep)
                    {
                        tickPositions.Add(i);
                        tickLabels.Add(dateStrings[i]);
                    }

                    if ((dateStrings.Length - 1) % tickStep != 0 && dateStrings.Length > 0)
                    {
                        tickPositions.Add(dateStrings.Length - 1);
                        tickLabels.Add(dateStrings.Last());
                    }

                    if (tickPositions.Any())
                    {
                        var tickGen = new ScottPlot.TickGenerators.NumericManual(tickPositions.ToArray(), tickLabels.ToArray());
                        _plot.Plot.Axes.Bottom.TickGenerator = tickGen;
                    }
                }

                // Настройки графика
                _plot.Plot.Title($"График эквити ({_tickerName}, {_timeFrame}) - P&L: {TotalProfit:F2} ₽     {_currentData}      ");

                // Автомасштабирование
                _plot.Plot.Axes.AutoScale();

                // Добавляем легенду
                _plot.Plot.Add.Legend();

                // Обновляем график
                _plot.Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка построения графика: {ex.Message}");
            }
        }

        private void SetFullHistory()
        {
            PlotEquity();
        }

        private void Refresh()
        {
            PlotEquity();
        }

        /// <summary>
        /// Экспортирует ВСЕ окно как изображение
        /// </summary>
        private void Export()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                    DefaultExt = "png",
                    FileName = $"Equity_Optimization_{DateTime.Now:yyyyMMdd_HHmmss}_{_tickerName}_{_timeFrame}"
                };

                if (dialog.ShowDialog() == true)
                {
                    // ✅ Получаем окно через визуальное дерево
                    var window = Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(w => w.DataContext == this);

                    if (window != null)
                    {
                        // ✅ Сохраняем ВСЕ окно целиком
                        SaveWindowAsImage(window, dialog.FileName);
                        MessageBox.Show($"График сохранен в: {dialog.FileName}", "Экспорт",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // ✅ Если окно не найдено - сохраняем только график как запасной вариант
                        _plot.Plot.SavePng(dialog.FileName, 1200, 800);
                        MessageBox.Show($"График сохранен в: {dialog.FileName}", "Экспорт",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка экспорта: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Сохраняет все окно как изображение
        /// </summary>
        private void SaveWindowAsImage(Window window, string fileName)
        {
            try
            {
                // ✅ Получаем размеры окна
                double width = window.ActualWidth;
                double height = window.ActualHeight;

                // ✅ Если окно еще не отрендерено - используем размеры по умолчанию
                if (width <= 0 || height <= 0)
                {
                    width = 900;
                    height = 550;
                }

                // ✅ Создаем RenderTargetBitmap для захвата всего окна
                var renderBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)width,
                    (int)height,
                    96d,
                    96d,
                    System.Windows.Media.PixelFormats.Pbgra32);

                // ✅ Рендерим все окно
                renderBitmap.Render(window);

                // ✅ Создаем PNG энкодер
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(renderBitmap));

                // ✅ Сохраняем в файл
                using (var fileStream = new System.IO.FileStream(fileName, System.IO.FileMode.Create))
                {
                    encoder.Save(fileStream);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения окна: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _plot?.Plot?.Clear();
        }
    }
}