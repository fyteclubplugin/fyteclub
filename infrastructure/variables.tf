variable "aws_region" {
  description = "AWS region for deployment"
  type        = string
  default     = "us-east-1"  # Free tier friendly
}

variable "project_name" {
  description = "Name of the project (used for resource naming)"
  type        = string
  default     = "fyteclub"
}

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
  default     = "prod"
}