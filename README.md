# SoftNodes

**SoftNodes** is the simulated PLC of [FactoryNodes](https://www.factorynodes.com). It runs the IEC 61131-3 program that FactoryNodes generates
(and ST written by hand) cycle by cycle, without a PLC, and serves its memory over **OPC UA**, **EtherNet/IP** and **Modbus TCP**, so
FactoryNodes, an HMI screen, a SCADA or another PLC connect to it as they would to a real one.

*SoftNodes es el PLC simulado de FactoryNodes: ejecuta el programa que genera FactoryNodes y lo sirve por OPC UA, EtherNet/IP y Modbus TCP, como un
PLC real. Cada instancia es una carpeta, como en PLCSIM Advanced.*

## What it does

- **Instances**: each one is a folder with its program (`programa.softnodes`, what FactoryNodes saves with *Export to PLC ▸ SoftNodes*), its
  settings and its retentive memory. Several at once, each on its own ports.
- **Power on / off, RUN / STOP**, reload the program in the folder (it reloads by itself when FactoryNodes transfers a new one, keeping the
  values of what did not change, like a download) and cold start.
- **Profiles**: `TIA` (an S7-1500) or `Sysmac` (an NX/NJ): how variables are named over OPC UA and which compatibility functions run.
- **Servers**: OPC UA (one port per instance), EtherNet/IP (explicit messaging, read and write by tag name, like an Omron NJ/NX) and Modbus TCP
  (the Modbus table of the FactoryNodes project: coils, discrete inputs, holding and input registers).
- **Variables**: see every variable of the program and write one by hand (like forcing an input).
- **Log**: power on, loads, stops, faults and the clients that connect.
- Six languages: Spanish, Catalan, English, French, German and Portuguese.

The plant simulation, the test bench (being the plant of a real PLC), the AI test operator and Factory Box monitoring are part of
**SoftNodes Pro**, which is not open source.

## Build

Windows, .NET 8 SDK:

```
dotnet build SoftNodes.sln
dotnet test SoftNodes.sln
dotnet run --project src/apps/SoftNodes.Open
```

`--instancias <folder>` uses another folder for the instances (by default `Documents\SoftNodes\instancias`).

## Layout

- `src/libs/FactoryNodes.SoftNodes.Core` — the ST interpreter, the program package and the instance (cycle, retentive memory, debugger core).
- `src/libs/FactoryNodes.SoftNodes.OpcUa`, `.Cip`, `.Modbus` — the three servers.
- `src/libs/FactoryNodes.SoftNodes.Instances` — an instance folder: settings, power, reload, servers.
- `src/libs/FactoryNodes.SoftNodes.Open.Presentation` — the view models (no WPF).
- `src/libs/FactoryNodes.Iec61131`, `FactoryNodes.Monitoring`, `FactoryNodes.Monitoring.Cip`, `FactoryNodes.Devices`, `FactoryNodes.Localization` — what
  they need: the library and program model, PLC addressing, the EtherNet/IP codes and the six languages.
- `src/apps/SoftNodes.Open` — the Windows (WPF) window.
- `tests/FactoryNodes.SoftNodes.Open.Tests` — an instance runs, serves OPC UA and EtherNet/IP and keeps its memory.

## License

**AGPL-3.0** — see [LICENSE](LICENSE). SoftNodes is dual licensed: AGPL-3.0 for everyone, and a commercial license from FactoryNodes for those
who cannot use the AGPL. Contributions need a contributor license agreement: see [CONTRIBUTING.md](CONTRIBUTING.md).
