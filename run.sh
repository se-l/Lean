#!/bin/bash
ip=$(dig +short @192.168.1.1 ${HOST_NAME})
#ip=$(dig +short @192.168.178.1 ${HOST_NAME})
mkdir -p /mnt/c
mkdir -p /mnt/d
mount -t cifs -o password=${PASSWD_MNT_C},vers=3.0 //${ip}/d /mnt/d
mount -t cifs -o password=${PASSWD_MNT_C},vers=3.0 //${ip}/c /mnt/c

mkdir -p /repos/quantconnect/Lean
ln -s /mnt/d/trade/data/ /repos/quantconnect/Lean

mkdir -p /repos/quantconnect/Lean/Launcher/bin
ln -s /mnt/c/repos/quantconnect/Lean/Launcher/bin/Analytics/ /repos/quantconnect/Lean/Launcher/bin

cd /repos/quantconnect/Lean/Launcher/bin/Debug/
dotnet QuantConnect.Lean.Launcher.dll
#/bin/bash
