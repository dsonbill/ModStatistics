import json
from functools import wraps
from flask import Blueprint, redirect, request, current_app
from flask_json import FlaskJSON, JsonError, json_response, as_json, as_json_p

api = Blueprint('api', __name__)

@api.route('/statistics/report', methods=['POST'])
def statistics_report():
    json_data = request.json
    print('Received statistics report from: ' + json_data['id'])
    return "OK"