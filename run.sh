#!/bin/bash
ip=$(dig +short @192.168.1.1 sebstrix)
mkdir -p /mnt/c
mount -t cifs -o password=${PASSWD_MNT_C},vers=3.0 //${ip}/c /mnt/c

mkdir -p /repos/quantconnect/Lean
ln -s /mnt/c/repos/trade/data/ /repos/quantconnect/Lean

mkdir -p /repos/quantconnect/Lean/Launcher/bin
ln -s /mnt/c/repos/quantconnect/Lean/Launcher/bin/Analytics/ /repos/quantconnect/Lean/Launcher/bin

cd /repos/quantconnect/Lean/Launcher/bin/Debug/
dotnet QuantConnect.Lean.Launcher.dll
#/bin/bash
