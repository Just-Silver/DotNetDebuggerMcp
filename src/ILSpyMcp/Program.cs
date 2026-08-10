using ILSpyMcp;
using Microsoft.Extensions.Hosting;

return await new HostBuilder().RunCommandLineApplicationAsync<ILSpyMcpCmd>(args);