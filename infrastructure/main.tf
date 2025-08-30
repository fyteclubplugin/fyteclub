terraform {
  required_version = ">= 1.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

# S3 bucket for mod files (free tier: 5GB)
resource "aws_s3_bucket" "mod_storage" {
  bucket = "${var.project_name}-mods-${random_id.bucket_suffix.hex}"
}

resource "random_id" "bucket_suffix" {
  byte_length = 4
}

resource "aws_s3_bucket_versioning" "mod_storage" {
  bucket = aws_s3_bucket.mod_storage.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "mod_storage" {
  bucket = aws_s3_bucket.mod_storage.id

  rule {
    id     = "delete_old_mods"
    status = "Enabled"

    expiration {
      days = 30  # Reduced to 30 days for more aggressive cleanup
    }

    noncurrent_version_expiration {
      noncurrent_days = 7
    }
  }

  rule {
    id     = "delete_by_size"
    status = "Enabled"
    
    # Delete objects older than 7 days when bucket gets large
    expiration {
      days = 7
    }
    
    filter {
      tag {
        key   = "cleanup-priority"
        value = "high"
      }
    }
  }
}

# DynamoDB tables (free tier: 25GB)
resource "aws_dynamodb_table" "players" {
  name           = "${var.project_name}-players"
  billing_mode   = "PAY_PER_REQUEST"  # Free tier friendly
  hash_key       = "player_id"
  range_key      = "sk"

  attribute {
    name = "player_id"
    type = "S"
  }

  attribute {
    name = "sk"
    type = "S"
  }

  tags = {
    Name = "FyteClub Players"
  }
}

resource "aws_dynamodb_table" "mods" {
  name           = "${var.project_name}-mods"
  billing_mode   = "PAY_PER_REQUEST"
  hash_key       = "mod_id"
  range_key      = "sk"

  attribute {
    name = "mod_id"
    type = "S"
  }

  attribute {
    name = "sk"
    type = "S"
  }

  tags = {
    Name = "FyteClub Mods"
  }
}

resource "aws_dynamodb_table" "groups" {
  name           = "${var.project_name}-groups"
  billing_mode   = "PAY_PER_REQUEST"
  hash_key       = "group_id"
  range_key      = "sk"

  attribute {
    name = "group_id"
    type = "S"
  }

  attribute {
    name = "sk"
    type = "S"
  }

  tags = {
    Name = "FyteClub Groups"
  }
}

# Lambda execution role
resource "aws_iam_role" "lambda_role" {
  name = "${var.project_name}-lambda-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "lambda.amazonaws.com"
        }
      }
    ]
  })
}

resource "aws_iam_role_policy" "lambda_policy" {
  name = "${var.project_name}-lambda-policy"
  role = aws_iam_role.lambda_role.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "logs:CreateLogGroup",
          "logs:CreateLogStream",
          "logs:PutLogEvents"
        ]
        Resource = "arn:aws:logs:*:*:*"
      },
      {
        Effect = "Allow"
        Action = [
          "dynamodb:GetItem",
          "dynamodb:PutItem",
          "dynamodb:UpdateItem",
          "dynamodb:DeleteItem",
          "dynamodb:Query",
          "dynamodb:Scan"
        ]
        Resource = [
          aws_dynamodb_table.players.arn,
          aws_dynamodb_table.mods.arn,
          aws_dynamodb_table.groups.arn
        ]
      },
      {
        Effect = "Allow"
        Action = [
          "s3:GetObject",
          "s3:PutObject",
          "s3:DeleteObject",
          "s3:ListBucket",
          "s3:GetBucketLocation",
          "s3:PutObjectTagging",
          "s3:GetObjectTagging"
        ]
        Resource = [
          aws_s3_bucket.mod_storage.arn,
          "${aws_s3_bucket.mod_storage.arn}/*"
        ]
      }
    ]
  })
}

# Lambda functions
resource "aws_lambda_function" "api_handler" {
  filename         = "lambda.zip"
  function_name    = "${var.project_name}-api"
  role            = aws_iam_role.lambda_role.arn
  handler         = "index.handler"
  runtime         = "nodejs20.x"
  timeout         = 30

  environment {
    variables = {
      PLAYERS_TABLE = aws_dynamodb_table.players.name
      MODS_TABLE    = aws_dynamodb_table.mods.name
      GROUPS_TABLE  = aws_dynamodb_table.groups.name
      MOD_BUCKET    = aws_s3_bucket.mod_storage.bucket
    }
  }

  depends_on = [data.archive_file.lambda_zip]
}

# Storage cleanup Lambda
resource "aws_lambda_function" "storage_cleanup" {
  filename         = "cleanup-lambda.zip"
  function_name    = "${var.project_name}-cleanup"
  role            = aws_iam_role.lambda_role.arn
  handler         = "cleanup.handler"
  runtime         = "nodejs20.x"
  timeout         = 300  # 5 minutes for cleanup operations

  environment {
    variables = {
      MOD_BUCKET = aws_s3_bucket.mod_storage.bucket
      SIZE_LIMIT_GB = "4.5"  # Start cleanup at 4.5GB to stay under 5GB
    }
  }

  depends_on = [data.archive_file.cleanup_lambda_zip]
}

# Create Lambda function packages
data "archive_file" "lambda_zip" {
  type        = "zip"
  output_path = "lambda.zip"
  source {
    content = templatefile("${path.module}/lambda/index.js", {
      players_table = aws_dynamodb_table.players.name
      mods_table    = aws_dynamodb_table.mods.name
      groups_table  = aws_dynamodb_table.groups.name
      mod_bucket    = aws_s3_bucket.mod_storage.bucket
    })
    filename = "index.js"
  }
}

data "archive_file" "cleanup_lambda_zip" {
  type        = "zip"
  output_path = "cleanup-lambda.zip"
  source_file = "${path.module}/lambda/cleanup.js"
  output_file_mode = "0666"
}

# API Gateway (free tier: 1M requests)
resource "aws_api_gateway_rest_api" "fyteclub_api" {
  name = "${var.project_name}-api"
  
  endpoint_configuration {
    types = ["REGIONAL"]
  }
}

resource "aws_api_gateway_resource" "api_resource" {
  rest_api_id = aws_api_gateway_rest_api.fyteclub_api.id
  parent_id   = aws_api_gateway_rest_api.fyteclub_api.root_resource_id
  path_part   = "{proxy+}"
}

resource "aws_api_gateway_method" "api_method" {
  rest_api_id   = aws_api_gateway_rest_api.fyteclub_api.id
  resource_id   = aws_api_gateway_resource.api_resource.id
  http_method   = "ANY"
  authorization = "NONE"
}

resource "aws_api_gateway_integration" "lambda_integration" {
  rest_api_id = aws_api_gateway_rest_api.fyteclub_api.id
  resource_id = aws_api_gateway_resource.api_resource.id
  http_method = aws_api_gateway_method.api_method.http_method

  integration_http_method = "POST"
  type                   = "AWS_PROXY"
  uri                    = aws_lambda_function.api_handler.invoke_arn
}

resource "aws_api_gateway_deployment" "api_deployment" {
  depends_on = [aws_api_gateway_integration.lambda_integration]

  rest_api_id = aws_api_gateway_rest_api.fyteclub_api.id
  stage_name  = "prod"
}

resource "aws_lambda_permission" "api_gateway" {
  statement_id  = "AllowExecutionFromAPIGateway"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api_handler.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_api_gateway_rest_api.fyteclub_api.execution_arn}/*/*"
}

# CloudWatch Event to trigger cleanup daily
resource "aws_cloudwatch_event_rule" "daily_cleanup" {
  name                = "${var.project_name}-daily-cleanup"
  description         = "Trigger storage cleanup daily"
  schedule_expression = "rate(1 day)"
}

resource "aws_cloudwatch_event_target" "cleanup_target" {
  rule      = aws_cloudwatch_event_rule.daily_cleanup.name
  target_id = "CleanupLambdaTarget"
  arn       = aws_lambda_function.storage_cleanup.arn
}

resource "aws_lambda_permission" "allow_cloudwatch" {
  statement_id  = "AllowExecutionFromCloudWatch"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.storage_cleanup.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.daily_cleanup.arn
}