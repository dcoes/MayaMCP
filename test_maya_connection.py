#!/usr/bin/env python
"""Test direct connection to Maya command port"""
import socket
import json

HOST = '127.0.0.1'
PORT = 60000

def send_command(command):
    """Send a Python command to Maya and get the result"""
    try:
        client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        client.connect((HOST, PORT))

        # Wrap in MEL python command
        mel_cmd = f'python("{command.replace(chr(34), chr(92)+chr(34))}")'

        client.send(mel_cmd.encode('utf-8'))
        result = client.recv(1024).decode('utf-8')
        client.close()

        print(f"Command: {command}")
        print(f"Result: {result}\n")
        return result
    except Exception as e:
        print(f"Error: {e}\n")
        return None

# Test commands
print("Testing Maya connection on port 60000...\n")

send_command("import maya.cmds as cmds")
send_command("cmds.polySphere(name='testSphere')")
send_command("cmds.polyCube(name='testCube', translate=[3, 0, 0])")
send_command("cmds.group(empty=True, name='testGroup')")

print("Done! Check your Maya viewport.")
