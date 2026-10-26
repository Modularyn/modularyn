# The container-build fixture: the smallest image on the pinned ASP.NET Core runtime base. CI builds it
# for linux/amd64 and requires its layers to be the base's and its runtimes to be the pinned release.
# Every Dockerfile in the repository must use this base by the same tag and index digest.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
USER $APP_UID
ENTRYPOINT ["dotnet", "--list-runtimes"]
