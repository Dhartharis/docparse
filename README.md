# DocParse

AI-powered document extraction and semantic search API built with .NET 8 and Clean Architecture.

## Overview

DocParse extracts structured data from documents (PDFs and images) using GPT-4o-mini, stores them in MongoDB, and enables semantic search via RAG (Retrieval-Augmented Generation) using pgvector on PostgreSQL.

## Tech Stack

- **Backend:** .NET 8, ASP.NET Core, Clean Architecture
- **AI:** OpenAI GPT-4o-mini, text-embedding-3-small
- **Storage:** MongoDB Atlas (documents), PostgreSQL + pgvector (embeddings)
- **DevOps:** Docker, GitHub Actions

## Features

- Extract structured fields from PDFs and images
- Automatic embedding generation on document upload
- Semantic search across all uploaded documents
- Natural language Q&A over your document history
- Export documents to CSV and Excel

## Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/document/extract` | Upload and extract document |
| GET | `/api/document/{id}` | Get document by ID |
| GET | `/api/document/all` | Get all documents |
| GET | `/api/document/{id}/export/csv` | Export to CSV |
| GET | `/api/document/{id}/export/excel` | Export to Excel |
| POST | `/api/query` | Ask questions about your documents |

## Setup

1. Clone the repository
2. Copy `appsettings.json` and create `appsettings.Development.json` with your credentials:

```json
{
  "OpenAI": {
    "ApiKey": "your-openai-api-key"
  },
  "MongoDB": {
    "ConnectionString": "your-mongodb-connection-string",
    "DatabaseName": "docparse",
    "CollectionName": "documents"
  },
  "PostgreSQL": {
    "ConnectionString": "your-postgresql-connection-string"
  }
}
```

3. Run the project:

```bash
dotnet run
```

4. Open Swagger at `http://localhost:5128/swagger`

## RAG Architecture

1. Document uploaded → GPT-4o-mini extracts fields → saved to MongoDB
2. Extracted content → OpenAI embeddings → saved to PostgreSQL/pgvector
3. User question → converted to vector → pgvector finds similar documents
4. Matching documents retrieved from MongoDB → passed as context to GPT → answer returned