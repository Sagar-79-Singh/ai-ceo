# AI CEO Orchestrator (C# / .NET)

## Overview
A web application built with ASP.NET Core that orchestrates AI agents in a strict Star Topology. It uses the Microsoft Agent Framework to connect to a local OmniRoute gateway via an OpenAI-compatible endpoint.

## Core Topology (STRICT)
- User talks ONLY to the CEO Agent.
- CEO plans tasks and delegates to Worker Agents sequentially.
- Worker Agents execute and return results ONLY to the CEO.
- Workers CANNOT talk to each other or spawn new tasks.
- CEO synthesizes the final output and presents it to the User.

## Features
1. **Agent Management**: Create, view, and edit agents (Name, Role, System Prompt, Model ID).
2. **OmniRoute Integration**: Native connection to `http://localhost:20128/v1` using the `OpenAI-DotNet` library.
3. **Execution Feed**: A UI showing the CEO's plan, worker delegations, and the final output.
