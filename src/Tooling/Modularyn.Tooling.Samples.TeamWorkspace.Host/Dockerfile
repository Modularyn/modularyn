FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY *.dll *.deps.json *.runtimeconfig.json deployment-lock.json ./
COPY wwwroot/sign-in.js ./wwwroot/sign-in.js
USER 1654:1654
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Modularyn.Tooling.Samples.TeamWorkspace.Host.dll"]
