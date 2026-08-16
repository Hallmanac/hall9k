using Spectre.Console.Cli;

CommandApp app = new();
app.Configure(config => config.SetApplicationName("h9k"));
return app.Run(args);
