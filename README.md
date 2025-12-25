<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" />
  <h2><strong>KzMapVote</strong></h2>
  <h3>Voting system for CS2KZ servers</h3>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/samayala22/CS2-KzMapVote/total" alt="Downloads">
  <img src="https://img.shields.io/github/stars/samayala22/CS2-KzMapVote?style=flat&logo=github" alt="Stars">
  <img src="https://img.shields.io/github/license/samayala22/CS2-KzMapVote" alt="License">
</p>

## Features

- !rtv system with center panel menu
- !nominate with partial map name matching or using workshop map ID
- Track other vote count for each map
- Fetches maps + tier from the CS2KZ global api directly

## Building

- Open the project in your preferred .NET IDE (e.g., Visual Studio, Rider, VS Code).
- Build the project. The output DLL and resources will be placed in the `build/` directory.
- The publish process will also create a zip file for easy distribution.

## Publishing

- Use the `dotnet publish -c Release` command to build and package your plugin.
- Distribute the generated zip file or the contents of the `build/publish` directory.