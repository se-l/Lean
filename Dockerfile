#
#   LEAN Docker Container 20200522
#   Cross platform deployment for multiple brokerages

# Use base system
FROM sebastianluen/lean:latest

MAINTAINER Sebastian Lueneburg <sebastian.lueneburg@gmail.com>

COPY ./Launcher/bin/Debug/ /repos/quantconnect/Lean/Launcher/bin/Debug/
RUN rm -f /repos/quantconnect/Lean/Launcher/bin/Debug/log.txt

# Prepare mounting external hard drive
RUN mkdir -p /repos/quantconnect/Lean/Data
COPY ./run.sh /run.sh
RUN chmod +x run.sh

ENTRYPOINT [ "./run.sh" ]
