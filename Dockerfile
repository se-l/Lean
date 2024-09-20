FROM sebastianluen/lean:latest

MAINTAINER Sebastian Lueneburg <sebastian.lueneburg@gmail.com>

COPY ./Launcher/bin/Release/ /repos/quantconnect/Lean/Launcher/bin/Release/
RUN rm -f /repos/quantconnect/Lean/Launcher/bin/Release/log.txt

WORKDIR /repos/quantconnect/Lean/Launcher/bin/Release

ENTRYPOINT [ "dotnet", "QuantConnect.Lean.Launcher.dll" ]
