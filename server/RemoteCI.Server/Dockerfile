# 多阶段构建：SDK 阶段编译，运行时阶段仅带 aspnet 运行时，适合 NAS/轻量服务器。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG REMOTECI_VERSION
WORKDIR /src

COPY shared/RemoteCI.Shared/ shared/RemoteCI.Shared/
COPY server/RemoteCI.Server/ server/RemoteCI.Server/
RUN if [ -n "$REMOTECI_VERSION" ]; then version_arg="-p:Version=$REMOTECI_VERSION"; else version_arg=""; fi; \
    dotnet publish server/RemoteCI.Server/RemoteCI.Server.csproj -c Release $version_arg -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG REMOTECI_VERSION
WORKDIR /app
COPY --from=build /app/publish .
# 入口脚本优先运行 WebUI 自更新安装到数据卷的版本（容器重建后仍然有效）。
# IMAGE_VERSION 附带构建时间：镜像任何一次重建都会让旧镜像上安装的自更新版本作废。
COPY server/RemoteCI.Server/docker-entrypoint.sh /usr/local/bin/remoteci-entrypoint
RUN sed -i 's/\r$//' /usr/local/bin/remoteci-entrypoint \
    && chmod 755 /usr/local/bin/remoteci-entrypoint \
    && printf '%s+%s\n' "${REMOTECI_VERSION:-dev}" "$(date -u +%Y%m%d%H%M%S)" > /app/IMAGE_VERSION

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Server__DatabasePath=/app/data/remoteci.db
VOLUME ["/app/data"]
ENTRYPOINT ["/usr/local/bin/remoteci-entrypoint"]
