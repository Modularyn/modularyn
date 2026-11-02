# The composition fixture image: a test-only image that CI builds and runs in its container-build job and never publishes.
# Its build context is the framework-dependent publish of the sample's test project, an xunit v3 executable that carries the
# sample host, the sample's modules and the AC-001 test modules A, B and C. Other planned test executables live under projects/.
# None of these, and no composition switch, is in
# the published sample image. Its only base is the pinned ASP.NET Core runtime image, by the same tag and index digest that
# every Dockerfile in the repository must use. Root owns the copied files, and the tests run as UID/GID 1654.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY . ./
USER 1654:1654
ENV MODULARYN_DEPLOYMENT_MODE=single-host-container
ENTRYPOINT ["dotnet", "Modularyn.Tooling.Samples.TeamWorkspace.Tests.dll"]
