FROM sebastianluen/lean:latest

MAINTAINER Sebastian Lueneburg <sebastian.lueneburg@gmail.com>

COPY ./Launcher/bin/Debug/ /repos/quantconnect/Lean/Launcher/bin/Debug/
RUN rm -f /repos/quantconnect/Lean/Launcher/bin/Debug/log.txt

COPY ./run.sh /run.sh
RUN chmod +x run.sh

ENTRYPOINT [ "/run.sh" ]
#ENTRYPOINT ["tail", "-f", "/dev/null"]
