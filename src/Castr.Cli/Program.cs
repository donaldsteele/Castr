using Castr.Cli;

return await CastrCli.BuildRootCommand().Parse(args).InvokeAsync();
