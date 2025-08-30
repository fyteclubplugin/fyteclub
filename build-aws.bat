@echo off
REM FyteClub AWS Terraform Build Script

echo 🌩️ Building FyteClub for AWS...

REM Check if Terraform is installed
terraform version >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ Terraform not found. Please install from https://terraform.io
    exit /b 1
)

REM Check if AWS CLI is configured
aws sts get-caller-identity >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ AWS CLI not configured. Run 'aws configure'
    exit /b 1
)

REM Navigate to infrastructure directory
cd infrastructure

REM Initialize Terraform
echo 📦 Initializing Terraform...
terraform init

REM Create terraform.tfvars if it doesn't exist
if not exist terraform.tfvars (
    echo 🔧 Creating terraform.tfvars...
    echo server_name = "My FyteClub Server" > terraform.tfvars
    echo enable_cleanup = true >> terraform.tfvars
    echo max_storage_gb = 5 >> terraform.tfvars
)

REM Plan deployment
echo 📋 Planning AWS deployment...
terraform plan

echo ✅ Ready to deploy to AWS!
echo 🚀 Run 'terraform apply' to deploy
echo 💰 Estimated cost: $0/month (free tier)
echo 🧹 Auto-cleanup enabled to prevent charges