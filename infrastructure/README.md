# StallionSync Infrastructure

Terraform infrastructure code for deploying StallionSync backend services optimized for AWS free tier.

## Components

- **API Gateway** - REST API endpoints (1M requests/month free)
- **Lambda Functions** - Serverless compute (1M requests/month free)
- **DynamoDB** - NoSQL database (25GB free)
- **S3** - Object storage for mod files (5GB free)

## Free Tier Optimization

**Cost**: $0/month for small groups (<50 users), ~$3-5/month for 100+ users

- DynamoDB: Pay-per-request billing (free tier covers small groups)
- S3: Lifecycle policies auto-delete old mods after 90 days
- Lambda: Optimized for minimal execution time
- No CloudFront (saves $8.50/month)

## Quick Deployment

### Prerequisites
- AWS CLI configured with credentials
- Terraform installed (>= 1.0)

### Deploy

```bash
# Clone and navigate
cd infrastructure

# Copy and customize variables
cp terraform.tfvars.example terraform.tfvars
# Edit terraform.tfvars with your settings

# Initialize and deploy
terraform init
terraform plan
terraform apply

# Get your API endpoint
terraform output api_endpoint
```

### Cleanup

```bash
# Destroy all resources (stops billing)
terraform destroy
```

## Architecture

```
FFXIV Client → StallionSync Client → API Gateway → Lambda → DynamoDB
                                           ↓
                                      S3 Storage
```

## API Endpoints

- `GET /api/v1/players/{id}/mods` - Get player's mod list
- `POST /api/v1/players/{id}/mods` - Update player's mods
- `GET /api/v1/mods/{id}/download` - Get mod download URL
- `POST /api/v1/groups/{id}/join` - Join sharing group

## Cost Monitoring

Monitor your AWS costs:
1. AWS Console → Billing Dashboard
2. Set up billing alerts for $5/month
3. Review S3 storage usage monthly