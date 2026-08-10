using ILSpyMcp;
using McMaster.Extensions.Hosting.CommandLine;
using Microsoft.Extensions.Hosting;

return await new HostBuilder().RunCommandLineApplicationAsync<ILSpyMcpCmd>(args);
