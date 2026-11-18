# DocParse

AI-powered document processing and semantic search API built with ASP.NET Core 8.

DocParse demonstrates how to combine document extraction, vector embeddings, and Retrieval-Augmented Generation (RAG) using a modular .NET backend.

## Tech Stack

- C# / .NET 8 / ASP.NET Core Web API
- OpenAI API (GPT-4o-mini and text-embedding-3-small)
- MongoDB for document storage
- PostgreSQL + pgvector for semantic search
- Entity Framework Core
- Swagger / OpenAPI

## Features

- Extract structured information from PDF and image files
- Identify invoices, receipts, and contracts
- Generate and store document embeddings
- Retrieve documents using vector similarity
- Answer natural-language questions using RAG
- Export extracted data to CSV and Excel

## Architecture

The project separates responsibilities into:

- Controllers: HTTP endpoints
- Application: extraction, embeddings, search, and export services
- Domain: document and embedding models
- Infrastructure: MongoDB, PostgreSQL, and OpenAI integrations

### Document Processing

1. Upload a PDF or image.
2. Extract structured fields using OpenAI.
3. Persist document metadata in MongoDB.
4. Generate vector embeddings.
5. Store embeddings in PostgreSQL with pgvector.

### Semantic Search

1. Receive a natural-language question.
2. Generate an embedding for the question.
3. Find similar documents using vector similarity.
4. Retrieve matching document data.
5. Generate an answer using the retrieved context.

## Getting Started

### Requirements

- .NET SDK 8
- MongoDB
- PostgreSQL with pgvector
- OpenAI API key

### Configuration

Set the following environment variables or use an ignored
appsettings.Development.json file:

- OpenAI__ApiKey
- MongoDB__ConnectionString
- PostgreSQL__ConnectionString

The MongoDB database and collection names are configured in appsettings.json.

Initialize PostgreSQL by running database/init.sql
against your application database.

### Run Locally

    dotnet restore
    dotnet build
    dotnet run -- --urls http://127.0.0.1:5128

Swagger UI (Development):

http://localhost:5128/swagger

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | /api/document/extract | Extract document data |
| GET | /api/document/all | List documents |
| GET | /api/document/{id} | Retrieve document |
| GET | /api/document/{id}/export/csv | Export CSV |
| GET | /api/document/{id}/export/excel | Export Excel |
| POST | /api/query | Ask questions using RAG |

## Project Status

This repository represents an early technical prototype of DocParse.

It is intended for educational and portfolio demonstration purposes.

It does not implement production authentication, authorization,
rate limiting, or comprehensive file-upload security controls.

Run locally with trusted test documents. Do not expose the API
directly to the public Internet.

OpenAI API usage may incur charges.

## Author

Dhartharis

GitHub: https://github.com/Dhartharis
