@echo off
REM FyteClub AWS Terraform Build Script
REM Deploy FyteClub server to AWS using Terraform infrastructure

title FyteClub AWS Setup
echo.
echo ===============================================
echo [*] FyteClub AWS Cloud Setup
echo ===============================================
echo Serverless mod sharing for AWS free tier
echo.

REM Check if Terraform is installed
echo [1/7] Checking Terraform installation...
terraform version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] ERROR: Terraform not found
    echo.
    echo Please install Terraform first:
    echo 1. Go to https://terraform.io/downloads
    echo 2. Download and install for Windows
    echo 3. Restart this script
    echo.
    pause
    exit /b 1
)
for /f "tokens=2" %%i in ('terraform version ^| findstr "Terraform"') do set TERRAFORM_VERSION=%%i
echo [OK] Terraform %TERRAFORM_VERSION% found

REM Check if AWS CLI is configured
echo [2/7] Checking AWS CLI configuration...
aws sts get-caller-identity >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ ERROR: AWS CLI not configured
    echo.
    echo Please configure AWS CLI first:
    echo 1. Run: aws configure
    echo 2. Enter your AWS access key and secret
    echo 3. Choose region (recommend us-east-1 for free tier)
    echo 4. Restart this script
    echo.
    pause
    exit /b 1
)
for /f "tokens=2 delims=:" %%i in ('aws sts get-caller-identity --query "Account" --output text 2^>nul') do set AWS_ACCOUNT=%%i
echo ✅ AWS CLI configured (Account: %AWS_ACCOUNT%)

REM Check if infrastructure directory exists
echo [3/7] Checking infrastructure files...
if not exist "infrastructure\main.tf" (
    echo ❌ ERROR: Infrastructure files not found
    echo Please run this script from the FyteClub root directory
    pause
    exit /b 1
)
echo ✅ Infrastructure files found

REM Navigate to infrastructure directory
cd infrastructure

REM Initialize Terraform
echo [4/7] Initializing Terraform...
terraform init
if %errorlevel% neq 0 (
    echo ❌ ERROR: Terraform initialization failed
    pause
    exit /b 1
)
echo ✅ Terraform initialized successfully

REM Create terraform.tfvars if it doesn't exist
echo [5/7] Configuring deployment variables...
if not exist terraform.tfvars (
    echo Creating terraform.tfvars...
    echo # FyteClub AWS Configuration > terraform.tfvars
    echo aws_region = "us-east-1" >> terraform.tfvars
    echo project_name = "fyteclub" >> terraform.tfvars
    echo environment = "prod" >> terraform.tfvars
    echo.
    echo ✅ Configuration file created
) else (
    echo ✅ Configuration file exists
)

REM Plan deployment
echo [6/7] Planning AWS deployment...

REM Check for existing infrastructure
echo [INFO] Checking for existing FyteClub infrastructure...
aws dynamodb describe-table --table-name fyteclub-players 2>nul >nul
if %errorlevel% equ 0 (
    echo [FOUND] Existing DynamoDB table: fyteclub-players
    echo [INFO] Terraform will manage existing resources
)

aws s3 ls s3://fyteclub-mods 2>nul >nul
if %errorlevel% equ 0 (
    echo [FOUND] Existing S3 bucket: fyteclub-mods
    echo [INFO] Terraform will manage existing resources
)

REM Check Lambda functions
aws lambda get-function --function-name fyteclub-api 2>nul >nul
if %errorlevel% equ 0 (
    echo [FOUND] Existing Lambda function: fyteclub-api
    echo [INFO] Terraform will update existing function
)

terraform plan
if %errorlevel% neq 0 (
    echo ❌ ERROR: Terraform planning failed
    echo Check the error messages above
    pause
    exit /b 1
)

echo [7/7] Deployment planning complete!
echo.
echo ===============================================
echo [*] FyteClub AWS Setup Ready!
echo ===============================================
echo.
echo � Deployment Summary:
echo   • Lambda Functions: API processing
echo   • DynamoDB Tables: Player and mod data  
echo   • S3 Bucket: Mod file storage
echo   • API Gateway: HTTP endpoints
echo   • CloudWatch: Automated cleanup
echo.
echo 💰 Cost Estimate:
echo   • Free Tier: $0/month for small groups
echo   • Beyond Free: ~$3-5/month for 100+ users
echo   • Auto-cleanup prevents runaway costs
echo.
echo [>] Next Steps:
echo   1. Review the plan above
echo   2. Run: terraform apply
echo   3. Share the API endpoint with friends
echo.
echo [WARN] WARNING: This will create AWS resources
echo   You can destroy them anytime with: terraform destroy
echo.