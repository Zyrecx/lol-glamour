if (args is ["--lane-backtest", var truth]) return Glamour.Host.LaneBacktest.Run(truth);

GlamourApp.Build(args).Run();
return 0;

public partial class Program;
