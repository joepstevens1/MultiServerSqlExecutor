# Multi-Server Sql Executor

The goal of this project is to provide a simple way to execute SQL queries across multiple database servers simultaneously. This can be particularly useful for database administrators and developers who need to manage and query multiple databases efficiently.

## Features

- Connects to azure sql servers
- Stores server connection details in a configuration file
- Executes SQL queries on multiple servers concurrently
- Store results from each server in separate files
- Combines all results into a single output file for easy analysis
- Uses CSV helper to handle CSV file operations
- Has a simple command-line interface for ease of use
    - CLI commands: 
    - `add-server`: Add a new server to the configuration file
    - `remove-server`: Remove a server from the configuration file
    - `list-servers`: List all configured servers
    - `execute-query`: Execute a SQL query on all configured servers and store results
      - '--queryFile': File that contains the SQL query to execute
      - '--outputFile': File to store the combined results
- Has a UI for ease of use
  - UI lanches when no CLI arguments are provided
  - Add, remove, and list servers
  - Input SQL queries and have syntax highlighting
  - View results and save to file

