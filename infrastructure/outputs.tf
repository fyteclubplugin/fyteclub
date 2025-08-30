output "api_endpoint" {
  description = "API Gateway endpoint URL"
  value       = aws_api_gateway_deployment.api_deployment.invoke_url
}

output "s3_bucket_name" {
  description = "S3 bucket name for mod storage"
  value       = aws_s3_bucket.mod_storage.bucket
}

output "dynamodb_tables" {
  description = "DynamoDB table names"
  value = {
    players = aws_dynamodb_table.players.name
    mods    = aws_dynamodb_table.mods.name
    groups  = aws_dynamodb_table.groups.name
  }
}

output "estimated_monthly_cost" {
  description = "Estimated monthly cost (USD)"
  value       = "Free tier: $0/month for <100 users, ~$3-5/month for 100+ users"
}